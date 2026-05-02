using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Core;

/// <summary>
/// Forward-only, read-only <see cref="Stream"/> view over an
/// <see cref="IVipsSource"/>. Decoders that accept <c>Stream</c>
/// (Magick.NET, JpegLibrary, …) can read directly from the source through
/// this adapter, skipping the <c>MemoryStream.ToArray()</c> buffering hop
/// that the byte[]-based <c>LoadAsync</c> path takes. Used by the
/// <c>LoadStreamingAsync</c> entry points on each loader.
///
/// <para>Constraints: <see cref="CanSeek"/> is false; the source might be
/// pipe-backed and unrewindable. Length is reported as -1 when the source
/// doesn't know its size. Closing the stream does not dispose the
/// underlying source — caller still owns the source's lifecycle.</para>
/// </summary>
public sealed class VipsSourceStream : Stream
{
    private readonly IVipsSource _source;

    public VipsSourceStream(IVipsSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => _source.Length;
    public override long Position
    {
        get => _source.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Sync over async — Magick.NET / JpegLibrary call Read synchronously,
        // and the async source ultimately reads from a PipeReader that has
        // already buffered data when the loader is invoked. The sync wait
        // here is the same one MemoryStream.Write would otherwise pay during
        // the byte[] drain.
        return _source.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
}

public static class VipsSourceExtensions
{
    /// <summary>
    /// Wrap this source as a forward-only <see cref="Stream"/>. The returned
    /// stream does not own the source — disposing it leaves the source's
    /// lifecycle to the caller.
    /// </summary>
    public static Stream AsStream(this IVipsSource source) => new VipsSourceStream(source);
}
