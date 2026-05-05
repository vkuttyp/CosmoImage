using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CosmoImage.Core;

/// <summary>
/// Spill-to-disk sink — port of libvips' <c>sinkdisc.c</c>. Materializes
/// a <see cref="VipsImage"/> to a temporary file in row-major order, then
/// returns a new <see cref="VipsImage"/> whose <c>GenerateFn</c> reads
/// tiles back from the file via concurrent-safe
/// <see cref="RandomAccess"/> calls.
///
/// <para>Use case: a long pipeline where one intermediate stage is too
/// big to materialize in RAM, but downstream stages only need random
/// access. Spilling that intermediate to disk frees memory at the
/// cost of (cold-cache) seek latency on subsequent reads. The OS
/// page-cache absorbs the working-set cost for adjacent reads.</para>
///
/// <para>Lifetime: the returned <see cref="Image"/> is only valid while
/// this sink is alive. Disposing closes the file handle and deletes
/// the temp file. Typical use:</para>
/// <code>
///   await using var sink = await DiscBackedSink.CreateAsync(largeImage);
///   var thumb = sink.Image.Resize(0.1);
///   await thumb.SaveJpegAsync(target);
///   // sink disposes here — temp file deleted.
/// </code>
/// </summary>
public sealed class DiscBackedSink : IAsyncDisposable
{
    private readonly string _tempPath;
    private readonly SafeFileHandle _handle;
    private readonly VipsImage _image;
    private bool _disposed;

    /// <summary>The disc-backed image. Lifetime is tied to this sink.</summary>
    public VipsImage Image => _image;

    /// <summary>Path of the temp file backing the image. Useful for diagnostics.</summary>
    public string TempPath => _tempPath;

    private DiscBackedSink(string tempPath, SafeFileHandle handle, VipsImage image)
    {
        _tempPath = tempPath;
        _handle = handle;
        _image = image;
    }

    /// <summary>
    /// Drain <paramref name="source"/> to a temp file and return a sink
    /// whose <see cref="Image"/> is backed by random-access reads from
    /// that file.
    /// </summary>
    public static async ValueTask<DiscBackedSink> CreateAsync(VipsImage source, CancellationToken cancellationToken = default)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        string tempPath = Path.GetTempFileName();

        // Phase 1: write source pixels to disk in row-major order via
        // OrderedStripSink. The strip callback writes synchronously to
        // a FileStream — we drop the stream as soon as the sink finishes.
        int srcW = source.Width;
        int srcH = source.Height;
        int srcPelSize = source.SizeOfPel;
        int srcRowBytes = srcW * srcPelSize;
        long expectedBytes = (long)srcRowBytes * srcH;

        await using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: false))
        {
            var sink = new OrderedStripSink(source, tileHeight: 16, (top, height, bytes) =>
            {
                fs.Write(bytes.Slice(0, srcRowBytes * height));
            });
            await sink.RunAsync(cancellationToken);
        }

        // Phase 2: open a read-only handle and build the random-access image.
        var handle = File.OpenHandle(tempPath, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);

        // ClientB carries the handle + per-image strides into the
        // GenerateFn closure. RandomAccess.Read is thread-safe so
        // concurrent workers can sample disjoint regions.
        var ctx = (handle, srcW, srcRowBytes, srcPelSize);

        var backing = new VipsImage
        {
            Width = source.Width, Height = source.Height,
            Bands = source.Bands, BandFormat = source.BandFormat,
            Interpretation = source.Interpretation, Coding = source.Coding,
            XRes = source.XRes, YRes = source.YRes,
            GenerateFn = Generate,
            ClientB = ctx,
        };
        backing.CopyMetadataFrom(source);
        backing.SetPipeline(VipsDemandStyle.Any);

        return new DiscBackedSink(tempPath, handle, backing);
    }

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var (handle, _, srcRowBytes, srcPelSize) =
            ((SafeFileHandle, int, int, int))b!;
        VipsRect r = outRegion.Valid;
        int regionRowBytes = r.Width * srcPelSize;

        // RandomAccess gives concurrent thread-safe reads off a single
        // SafeFileHandle — exactly what the multi-worker pipeline needs.
        for (int y = 0; y < r.Height; y++)
        {
            long fileOffset = (long)(r.Top + y) * srcRowBytes + (long)r.Left * srcPelSize;
            var addr = outRegion.GetAddress(r.Left, r.Top + y);
            int read = RandomAccess.Read(handle, addr.Slice(0, regionRowBytes), fileOffset);
            if (read != regionRowBytes) return -1;
        }
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
        try { File.Delete(_tempPath); }
        catch { /* best-effort cleanup; another holder may have the file open */ }
        await ValueTask.CompletedTask;
    }
}
