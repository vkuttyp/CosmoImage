using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CosmoImage.Core;

/// <summary>
/// Drives a pipeline by enumerating output tiles, dispatching them to a pool of
/// worker tasks, and calling <see cref="ConsumeTile"/> for each. Each worker
/// owns a private <see cref="VipsRegion"/> reused across tiles, and runs
/// Prepare without inner parallelism — parallelism comes from N workers each
/// preparing different tiles concurrently. Mirrors the libvips
/// vips_sink / vips_threadpool_run model.
/// </summary>
public abstract class VipsSink
{
    protected readonly VipsImage Image;
    protected readonly int TileWidth;
    protected readonly int TileHeight;

    protected VipsSink(VipsImage image, int tileWidth, int tileHeight)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        if (tileWidth <= 0) throw new ArgumentOutOfRangeException(nameof(tileWidth));
        if (tileHeight <= 0) throw new ArgumentOutOfRangeException(nameof(tileHeight));

        TileWidth = Math.Min(tileWidth, image.Width);
        TileHeight = Math.Min(tileHeight, image.Height);
    }

    /// <summary>
    /// Resolve a (tileWidth, tileHeight) from <paramref name="image"/>'s
    /// <see cref="VipsImage.DemandHint"/>. Mirrors libvips' tile-shape table:
    /// SmallTile → 128×128, FatStrip → W×16, ThinStrip → W×1, Any → W×16.
    /// </summary>
    protected static (int W, int H) TileSizeFromHint(VipsImage image)
    {
        return image.DemandHint switch
        {
            VipsDemandStyle.SmallTile => (128, 128),
            VipsDemandStyle.ThinStrip => (image.Width, 1),
            VipsDemandStyle.FatStrip => (image.Width, 16),
            _ => (image.Width, 16), // Any: pick a reasonable fat-ish strip
        };
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (Image.Width <= 0 || Image.Height <= 0) return;

        int dop = Math.Max(1, Environment.ProcessorCount);
        var tiles = Channel.CreateBounded<VipsRect>(new BoundedChannelOptions(dop * 2)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producer = Task.Run(async () =>
        {
            try
            {
                for (int y = 0; y < Image.Height; y += TileHeight)
                {
                    for (int x = 0; x < Image.Width; x += TileWidth)
                    {
                        var r = new VipsRect(
                            x, y,
                            Math.Min(TileWidth, Image.Width - x),
                            Math.Min(TileHeight, Image.Height - y));
                        await tiles.Writer.WriteAsync(r, ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                tiles.Writer.TryComplete();
            }
        }, ct);

        var workers = Enumerable.Range(0, dop).Select(id => Task.Run(async () =>
        {
            using var region = new VipsRegion(Image);

            await foreach (var tile in tiles.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (region.Prepare(tile) != 0)
                    throw new InvalidOperationException($"Prepare failed at {tile.Left},{tile.Top} {tile.Width}x{tile.Height}");

                ConsumeTile(id, region, tile);
            }
        }, ct)).ToArray();

        await Task.WhenAll(workers.Concat(new[] { producer })).ConfigureAwait(false);
    }

    /// <summary>
    /// Called concurrently from worker threads. <c>region.Valid == tile</c>.
    /// The region's buffer is reused for the next tile by this worker, so any
    /// data the implementation needs to keep beyond this call must be copied.
    /// Implementations are responsible for thread-safety of their own state.
    /// </summary>
    protected abstract void ConsumeTile(int workerId, VipsRegion region, VipsRect tile);
}

/// <summary>
/// Sink variant that enforces top-down row order on output. Tiles are gathered
/// from workers into a small reorder buffer keyed by <c>tile.Top</c>; the
/// consumer callback runs single-threaded, in strict row order, while workers
/// continue preparing further tiles in parallel. Equivalent to the write_thread
/// in libvips vips_sink_disc.
/// </summary>
public sealed class OrderedStripSink : VipsSink
{
    /// <summary>
    /// Receives a strip of finalized pixels in top-down order. <c>bytes</c> is
    /// a span over a pool-rented buffer whose physical length may exceed the
    /// logical strip size — only the supplied span is valid contents. The
    /// callback must finish using the data before returning; the sink will
    /// reclaim the underlying buffer immediately afterward.
    /// </summary>
    public delegate void StripConsumer(int top, int height, ReadOnlySpan<byte> bytes);

    private readonly StripConsumer _onStripReady;
    private readonly object _lock = new();
    // Tuple holds the pool-rented buffer plus the logical byte count; the
    // physical length (Bytes.Length) can be larger than ByteCount when the
    // allocator returns an oversized buffer.
    private readonly SortedDictionary<int, (int Height, byte[] Bytes, int ByteCount)> _pending = new();
    private int _nextTop;

    /// <param name="image">Image to drain.</param>
    /// <param name="tileHeight">Strip height. Tile width is always image width.</param>
    /// <param name="onStripReady">
    /// Called single-threaded under an internal lock, in strict top-down order.
    /// <c>bytes</c> is exactly <c>Image.SizeOfPel * Image.Width * height</c> long
    /// and is owned by the callback (the sink will not touch it after the call).
    /// </param>
    public OrderedStripSink(VipsImage image, int tileHeight, StripConsumer onStripReady)
        : base(image, image.Width, tileHeight)
    {
        _onStripReady = onStripReady ?? throw new ArgumentNullException(nameof(onStripReady));
    }

    /// <summary>
    /// Tile height is inferred from the image's <see cref="VipsImage.DemandHint"/>:
    /// ThinStrip→1, otherwise 16. SmallTile pipelines pay an efficiency cost in
    /// this sink because it's locked to full-width strips for ordered output.
    /// </summary>
    public OrderedStripSink(VipsImage image, StripConsumer onStripReady)
        : this(image, TileSizeFromHint(image).H, onStripReady)
    {
    }

    protected override void ConsumeTile(int workerId, VipsRegion region, VipsRect tile)
    {
        int rowBytes = tile.Width * Image.SizeOfPel;
        int byteCount = rowBytes * tile.Height;
        // Rent from the image's allocator. Pool buffers may be oversized
        // (e.g. ArrayPool rounds up to the next power of two); the consumer
        // is handed a span sized to byteCount so it never sees the slack.
        var copy = Image.Allocator.Rent(byteCount);
        for (int row = 0; row < tile.Height; row++)
        {
            var src = region.GetAddress(tile.Left, tile.Top + row);
            src.Slice(0, rowBytes).CopyTo(copy.AsSpan(row * rowBytes, rowBytes));
        }

        lock (_lock)
        {
            _pending.Add(tile.Top, (tile.Height, copy, byteCount));
            while (_pending.TryGetValue(_nextTop, out var ready))
            {
                _onStripReady(_nextTop, ready.Height, ready.Bytes.AsSpan(0, ready.ByteCount));
                _pending.Remove(_nextTop);
                _nextTop += ready.Height;
                Image.Allocator.Return(ready.Bytes);
            }
        }
    }
}

/// <summary>
/// Materializes the entire image into a flat row-major byte buffer. Tile shape
/// is taken from <see cref="VipsImage.DemandHint"/>, so SmallTile pipelines
/// (Affine, Resize) actually run with 128×128 tiles end-to-end instead of being
/// locked to full-width strips. Each worker writes its tile into a disjoint
/// region of the shared buffer, no locking needed. Equivalent to libvips
/// <c>vips_sink_memory</c>.
/// </summary>
public sealed class MemorySink : VipsSink
{
    private readonly byte[] _buffer;
    private readonly int _stride;

    /// The materialized buffer. Valid after <see cref="VipsSink.RunAsync"/>
    /// completes. Layout: row-major, contiguous, stride = Width × SizeOfPel.
    public byte[] Pixels => _buffer;

    public MemorySink(VipsImage image)
        : this(image, TileSizeFromHint(image).W, TileSizeFromHint(image).H)
    {
    }

    public MemorySink(VipsImage image, int tileWidth, int tileHeight)
        : base(image, tileWidth, tileHeight)
    {
        _stride = image.Width * image.SizeOfPel;
        _buffer = new byte[_stride * image.Height];
    }

    protected override void ConsumeTile(int workerId, VipsRegion region, VipsRect tile)
    {
        int rowBytes = tile.Width * Image.SizeOfPel;
        for (int row = 0; row < tile.Height; row++)
        {
            var src = region.GetAddress(tile.Left, tile.Top + row);
            int dstOffset = (tile.Top + row) * _stride + tile.Left * Image.SizeOfPel;
            src.Slice(0, rowBytes).CopyTo(_buffer.AsSpan(dstOffset, rowBytes));
        }
    }
}
