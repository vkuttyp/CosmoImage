using System;
using System.Buffers;

namespace CosmoImage.Core;

/// <summary>
/// Backing allocator for transient pixel buffers (VipsRegion working memory,
/// per-tile copies in OrderedStripSink). Pool implementations may return
/// oversized buffers; callers must track logical length separately and never
/// rely on <c>buffer.Length</c> as the contents length.
///
/// Long-lived buffers (loader-side <see cref="VipsImage.PixelsLazy"/>,
/// <see cref="MemorySink.Pixels"/>) deliberately bypass this allocator —
/// pool ownership across an image's full lifetime is hard to guarantee, and
/// the GC handles those fine.
/// </summary>
public interface IVipsAllocator
{
    /// <summary>Rent a buffer of at least <paramref name="minLength"/> bytes.</summary>
    byte[] Rent(int minLength);

    /// <summary>Return a buffer previously obtained from <see cref="Rent"/>.</summary>
    void Return(byte[] buffer);
}

/// <summary>
/// Default: <see cref="ArrayPool{T}.Shared"/>. Returns buffers without
/// clearing them — the pipeline overwrites every byte before reading.
/// </summary>
public sealed class ArrayPoolAllocator : IVipsAllocator
{
    public static readonly ArrayPoolAllocator Shared = new();
    private readonly ArrayPool<byte> _pool;
    public ArrayPoolAllocator(ArrayPool<byte>? pool = null) { _pool = pool ?? ArrayPool<byte>.Shared; }
    public byte[] Rent(int minLength) => _pool.Rent(Math.Max(1, minLength));
    public void Return(byte[] buffer) => _pool.Return(buffer);
}

/// <summary>
/// Plain <c>new byte[]</c> allocator — equivalent to the pre-allocator
/// behavior. Useful in tests or when pooling is undesirable. Buffers
/// returned to <see cref="Return"/> are simply dropped to the GC.
/// </summary>
public sealed class BareAllocator : IVipsAllocator
{
    public static readonly BareAllocator Shared = new();
    public byte[] Rent(int minLength) => new byte[Math.Max(1, minLength)];
    public void Return(byte[] buffer) { /* GC */ }
}
