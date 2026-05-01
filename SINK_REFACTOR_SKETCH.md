# Sink-driven threadpool refactor (sketch)

## Goal
Move parallelism out of `VipsRegion.Prepare` and into a single driver at the
sink end of the pipeline. Make every `Generate` → `Prepare` step run
synchronously on one worker thread; let parallelism come from N workers each
draining different output tiles concurrently. Mirrors libvips
`vips_sink` / `vips_sink_disc` over `vips_threadpool_run`.

## Why
Today `VipsRegion.Prepare` forks `Environment.ProcessorCount` Tasks per call.
Because `Generate` recursively calls `inRegion.Prepare(...)`, every pipeline
stage forks again — `cores^depth` over-subscription, no cache reuse, and
savers that prepare row-by-row pay fork overhead per row.

## Shape of the change

```
BEFORE                                     AFTER
saver loop                                  saver loop
  for y in 0..H:                              sink.RunAsync()
    region.Prepare(0,y,W,1)        →            ├ worker 0: prepare(tile_a) → consume
      └ ParallelFor strips                      ├ worker 1: prepare(tile_b) → consume
          generate                              ├ worker 2: ...
            in.Prepare → ParallelFor …          └ each Prepare is single-threaded
              (cores^depth tasks)
```

## Step 1 — Strip parallelism out of `VipsRegion.Prepare`

`VipsRegion.cs` becomes plain:

```csharp
public int Prepare(VipsRect r)
{
    if (r.IsEmpty) return 0;
    if (_isAlias) throw new InvalidOperationException("Cannot Prepare an alias region");

    int requiredBpl = r.Width * Image.SizeOfPel;
    int requiredSize = requiredBpl * r.Height;

    if (_buffer == null || _buffer.Length < requiredSize)
        _buffer = new byte[requiredSize];

    _bpl = requiredBpl;
    Valid = r;
    _originX = r.Left;
    _originY = r.Top;

    if (Image.GenerateFn != null)
    {
        bool stop = false;
        // _seq is per-region; populated by start_fn (Step 3), null for now.
        Image.GenerateFn(this, _seq, Image.ClientA, Image.ClientB, ref stop);
    }
    return 0;
}
```

No `Parallel.For`. No alias sub-regions. `Generate` recursion is sequential
on whatever thread called `Prepare`.

## Step 2 — Add `VipsSink` (the new driver)

```csharp
// VipsSink.cs
public abstract class VipsSink
{
    protected readonly VipsImage Image;
    protected readonly int TileWidth;
    protected readonly int TileHeight;

    protected VipsSink(VipsImage image, int tileW, int tileH)
    {
        Image = image; TileWidth = tileW; TileHeight = tileH;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        int dop = Math.Max(1, Environment.ProcessorCount);
        var tiles = Channel.CreateBounded<VipsRect>(dop * 2);

        var producer = Task.Run(async () =>
        {
            for (int y = 0; y < Image.Height; y += TileHeight)
            for (int x = 0; x < Image.Width;  x += TileWidth)
            {
                var r = new VipsRect(x, y,
                    Math.Min(TileWidth,  Image.Width  - x),
                    Math.Min(TileHeight, Image.Height - y));
                await tiles.Writer.WriteAsync(r, ct);
            }
            tiles.Writer.Complete();
        }, ct);

        var workers = Enumerable.Range(0, dop).Select(id => Task.Run(async () =>
        {
            // Per-worker output region — reused for every tile this worker pulls.
            using var region = new VipsRegion(Image);
            await foreach (var tile in tiles.Reader.ReadAllAsync(ct))
            {
                if (region.Prepare(tile) != 0)
                    throw new InvalidOperationException($"Prepare failed at {tile}");
                ConsumeTile(id, region, tile);
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers.Append(producer));
    }

    /// Called concurrently from worker threads.
    /// `region.Valid == tile`. Implementations must be thread-safe w.r.t. their own state.
    protected abstract void ConsumeTile(int workerId, VipsRegion region, VipsRect tile);
}
```

Workers each own their output `VipsRegion`, so the buffer is reused tile-to-tile
for the same worker. `Generate`'s own recursion still creates input regions on
the stack — Step 3 fixes that.

## Step 3 — Per-worker `seq` (start_fn / stop_fn)

Add to `VipsImage`:

```csharp
internal Func<VipsImage, object?, object?, object?>? StartFn { get; set; }
internal Action<object?, object?, object?>?         StopFn  { get; set; }
```

Region holds a `_seq` populated lazily on first `Prepare` (calling `StartFn`)
and disposed on `Dispose` (calling `StopFn`). Operations stash their reusable
input `VipsRegion`(s) in `seq` and reuse them in `Generate` instead of
`new VipsRegion(in)` per call. Direct port of `vips_start_one` /
`vips_stop_one`.

## Step 4 — Sequential ordering for savers

PNG/JPEG/WebP need rows top-down. Two patterns:

**A. Strip-shaped tile + ordered consumer.** Pick `TileWidth = Image.Width`,
`TileHeight = 16`. Workers may finish out of order; have `ConsumeTile` push
into a `SortedDictionary<int, byte[]>` keyed by `tile.Top`, and a single
emitter thread drains it in order, writing to the encoder. This is what
`vips_sink_disc` does (its "write_thread").

**B. Push-style.** If the encoder API needs strict serial calls (libpng,
libjpeg), keep workers producing into a reorder buffer; one writer thread
calls the encoder. Workers never touch the encoder.

Skeleton for the saver:

```csharp
internal sealed class PngWriteSink : VipsSink
{
    private readonly PipeWriter _writer;
    private readonly SortedDictionary<int, byte[]> _pending = new();
    private int _nextRow = 0;
    private readonly object _lock = new();

    public PngWriteSink(VipsImage image, PipeWriter writer)
        : base(image, image.Width, tileH: 16) => _writer = writer;

    protected override void ConsumeTile(int _, VipsRegion region, VipsRect tile)
    {
        // Copy pixels out of the worker's region buffer (it'll be reused for the next tile).
        var copy = region.Valid.Width * region.Valid.Height * Image.SizeOfPel;
        var bytes = new byte[copy];
        // ... copy strip pixels into `bytes` ...

        lock (_lock)
        {
            _pending[tile.Top] = bytes;
            while (_pending.TryGetValue(_nextRow, out var ready))
            {
                EncodeStrip(_writer, _nextRow, ready);  // libpng row writes
                _pending.Remove(_nextRow);
                _nextRow += tile.Height;
            }
        }
    }
}
```

Then `VipsPngSaver.SaveAsync` becomes `new PngWriteSink(image, writer).RunAsync()`
plus PNG header/trailer scaffolding around it.

## Step 5 — Tile size from demand hint (deferred)

For now hard-code `(W, 16)` — fat strips. Once `VipsImage.DemandHint` lands
(separate work item), swap:

| hint        | tile     |
|-------------|----------|
| THINSTRIP   | (W, 1)   |
| FATSTRIP    | (W, 16)  |
| SMALLTILE   | (128,128)|
| ANY         | inherit  |

## Migration order
1. Land `VipsSink` (Step 2) and a single saver (PNG) on the new path.
2. Strip the `Parallel.For` from `VipsRegion.Prepare` (Step 1) — only safe
   *after* every saver/materializer goes through `VipsSink`, otherwise
   throughput collapses.
3. Add `StartFn`/`StopFn` and migrate hot ops (Conv, Linear, Affine) to reuse
   per-worker input regions (Step 3).
4. Migrate JPEG/WebP/TIFF savers.
5. Add `DemandHint` and tile-shape selection (Step 5).

## What this does not change
- `VipsImage` / `VipsRegion` / `Generate` delegate signature — additive only.
- The recursive lazy contract of `Generate` calling `inRegion.Prepare`.
- `VipsCache` — operation-level memoization is orthogonal.
- `GetAddress` clamping (separate fix; flagged item 6 in prior review).
