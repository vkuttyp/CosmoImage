using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Core;

public class PipeVipsSource : IVipsSource
{
    private readonly PipeReader _reader;
    private long _position;

    public PipeVipsSource(PipeReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public long Length => -1;
    public long Position => _position;
    public bool IsMappable => false;

    public async ValueTask<ReadOnlyMemory<byte>> SniffAsync(int length, CancellationToken cancellationToken = default)
    {
        var result = await _reader.ReadAsync(cancellationToken);
        var buffer = result.Buffer;

        if (buffer.Length < length && !result.IsCompleted)
        {
            // If we don't have enough data yet, we need to advance examined and try again
            // This is a bit complex for a simple SniffAsync, so we'll just return what we have
            // and the caller (like IsJpegAsync) can decide.
        }

        int toSniff = (int)Math.Min(buffer.Length, length);
        var data = buffer.Slice(0, toSniff).ToArray();
        
        // Important: Advance only examined, not consumed
        _reader.AdvanceTo(buffer.Start, buffer.GetPosition(toSniff));

        return data;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var result = await _reader.ReadAsync(cancellationToken);
        var bufferSequence = result.Buffer;

        if (bufferSequence.IsEmpty && result.IsCompleted)
        {
            _reader.AdvanceTo(bufferSequence.Start);
            return 0;
        }

        int toCopy = (int)Math.Min(bufferSequence.Length, buffer.Length);
        bufferSequence.Slice(0, toCopy).CopyTo(buffer.Span);
        
        // Advance consumed
        _reader.AdvanceTo(bufferSequence.GetPosition(toCopy));
        _position += toCopy;

        return toCopy;
    }

    public ValueTask<long> SeekAsync(long offset, System.IO.SeekOrigin origin, CancellationToken cancellationToken = default)
    {
        if (origin == System.IO.SeekOrigin.Current && offset == 0)
            return new ValueTask<long>(_position);
            
        throw new NotSupportedException("Seeking is not supported on PipeVipsSource");
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.CompleteAsync();
    }
}
