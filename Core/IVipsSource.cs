using System;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Core;

public interface IVipsSource : IAsyncDisposable
{
    ValueTask<ReadOnlyMemory<byte>> SniffAsync(int length, CancellationToken cancellationToken = default);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    ValueTask<long> SeekAsync(long offset, System.IO.SeekOrigin origin, CancellationToken cancellationToken = default);
    bool IsMappable { get; }
    long Length { get; }
    long Position { get; }
}
