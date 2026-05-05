using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Core;

/// <summary>
/// Output abstraction — the dual of <see cref="IVipsSource"/>. Mirrors
/// libvips' <c>VipsTarget</c>: a place savers can write to without
/// caring whether the destination is a pipe, a memory buffer, a file,
/// or a custom callback.
///
/// <para>Existing savers take <see cref="PipeWriter"/> directly — call
/// <see cref="VipsTargetExtensions.AsPipeWriter"/> on an
/// <see cref="IVipsTarget"/> to adapt; or use
/// <see cref="MemoryVipsTarget"/> / <see cref="StreamVipsTarget"/> /
/// <see cref="CallbackVipsTarget"/> as PipeWriter drop-ins via the
/// same extension.</para>
/// </summary>
public interface IVipsTarget : IAsyncDisposable
{
    /// <summary>Append <paramref name="data"/> to the target.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Flush any internal buffers down to the underlying sink.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Signal completion. After this call no further writes are permitted.
    /// Implementations can use this to commit final state (e.g.,
    /// <see cref="MemoryVipsTarget.ToArray"/>).
    /// </summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Total bytes written so far. Useful for progress reporting.</summary>
    long Position { get; }
}

/// <summary>
/// In-memory target. Collects all writes into a <see cref="MemoryStream"/>;
/// the final buffer is available via <see cref="ToArray"/> after
/// <see cref="CompleteAsync"/>. Equivalent to libvips' "memory target".
/// </summary>
public sealed class MemoryVipsTarget : IVipsTarget
{
    private readonly MemoryStream _ms = new();
    private bool _completed;

    public long Position => _ms.Position;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_completed) throw new InvalidOperationException("MemoryVipsTarget already completed.");
        _ms.Write(data.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        _completed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Snapshot of all bytes written so far. Safe to call before or after <see cref="CompleteAsync"/>.</summary>
    public byte[] ToArray() => _ms.ToArray();

    public ValueTask DisposeAsync()
    {
        _ms.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Stream-backed target. Wraps any <see cref="Stream"/> with optional
/// ownership: when <paramref name="leaveOpen"/> is false, dispose
/// closes the underlying stream too.
/// </summary>
public sealed class StreamVipsTarget : IVipsTarget
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private long _position;

    public StreamVipsTarget(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!_stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        _leaveOpen = leaveOpen;
    }

    public long Position => _position;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _stream.WriteAsync(data, cancellationToken);
        _position += data.Length;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        => new(_stream.FlushAsync(cancellationToken));

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _stream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (!_leaveOpen) _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Callback-driven target. Forwards each write to a user-supplied
/// async delegate — the .NET equivalent of libvips' custom-target
/// write callback. Useful for streaming output to non-Stream sinks
/// (HTTP responses, native interop, encryption layers, etc.).
/// </summary>
public sealed class CallbackVipsTarget : IVipsTarget
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _onWrite;
    private readonly Func<CancellationToken, ValueTask>? _onFlush;
    private readonly Func<CancellationToken, ValueTask>? _onComplete;
    private long _position;

    public CallbackVipsTarget(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onWrite,
        Func<CancellationToken, ValueTask>? onFlush = null,
        Func<CancellationToken, ValueTask>? onComplete = null)
    {
        _onWrite = onWrite ?? throw new ArgumentNullException(nameof(onWrite));
        _onFlush = onFlush;
        _onComplete = onComplete;
    }

    public long Position => _position;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _onWrite(data, cancellationToken);
        _position += data.Length;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        => _onFlush != null ? _onFlush(cancellationToken) : ValueTask.CompletedTask;

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        => _onComplete != null ? _onComplete(cancellationToken) : ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Bridge to the existing <see cref="PipeWriter"/>-taking saver
/// surface. Saver call sites can stay on PipeWriter; callers building
/// pipelines around <see cref="IVipsTarget"/> just call
/// <see cref="AsPipeWriter"/>.
/// </summary>
public static class VipsTargetExtensions
{
    /// <summary>
    /// Adapt this target as a <see cref="PipeWriter"/>. Use to drive
    /// the existing PipeWriter-taking savers (PNG / JPEG / TIFF / etc.)
    /// from any <see cref="IVipsTarget"/>.
    /// </summary>
    public static PipeWriter AsPipeWriter(this IVipsTarget target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        return PipeWriter.Create(new TargetStream(target));
    }

    /// <summary>
    /// Stream adapter that funnels writes into an
    /// <see cref="IVipsTarget"/>. Synchronous Write is forwarded to the
    /// async target via blocking on the ValueTask — fine for our
    /// pipeline use where saver writes happen on a dedicated worker.
    /// </summary>
    private sealed class TargetStream : Stream
    {
        private readonly IVipsTarget _target;
        public TargetStream(IVipsTarget target) { _target = target; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _target.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Synchronous path — block on the async write since saver
            // worker threads expect Stream.Write to be synchronous.
            _target.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => await _target.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _target.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override void Flush()
            => _target.FlushAsync().AsTask().GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => _target.FlushAsync(cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
