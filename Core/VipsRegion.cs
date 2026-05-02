using System;
using System.Diagnostics;

namespace CosmoImage.Core;

public class VipsRegion : IDisposable
{
    public VipsImage Image { get; }
    public VipsRect Valid { get; private set; }

    private byte[]? _buffer;
    private int _bpl;
    private bool _isAlias;
    private int _originX;
    private int _originY;

    // Set when _buffer was rented from Image.Allocator (so Dispose / regrow
    // returns it). Stays false for aliased buffers (the internal ctor and
    // the memory-backed Image.Pixels fast-path), which the region doesn't own.
    private bool _ownsBuffer;

    // Per-region "seq" cookie produced by Image.StartFn on first Prepare and
    // freed by Image.StopFn (or, if the cookie is IDisposable, Dispose) when
    // this region is disposed. Lets operations cache per-thread state — most
    // commonly an input VipsRegion — so the Generate function reuses it across
    // tiles instead of allocating per call. Mirrors libvips vips_start_one /
    // vips_stop_one.
    private object? _seq;
    private bool _seqStarted;

    public VipsRegion(VipsImage image)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
    }

    internal VipsRegion(VipsImage image, byte[] buffer, int bpl, VipsRect valid, int originX, int originY)
    {
        Image = image;
        _buffer = buffer;
        _bpl = bpl;
        Valid = valid;
        _originX = originX;
        _originY = originY;
        _isAlias = true;
    }

    public Span<byte> GetAddress(int x, int y)
    {
        // No silent clamping. Callers must ensure (x, y) is inside Valid (or
        // inside the whole image, for memory-backed regions where the buffer
        // covers more than Valid). Out-of-buffer reads will throw via Span's
        // own bounds check; the debug assertion gives a clearer message.
        // Mirrors libvips VIPS_REGION_ADDR (g_assert in debug, no clamp in release).
        Debug.Assert(
            x >= Valid.Left && x < Valid.Right && y >= Valid.Top && y < Valid.Bottom,
            $"GetAddress({x}, {y}) outside Valid [{Valid.Left},{Valid.Top} {Valid.Width}x{Valid.Height}]");

        int offset = (y - _originY) * _bpl + (x - _originX) * Image.SizeOfPel;
        return _buffer.AsSpan(offset);
    }

    public int Prepare(VipsRect r)
    {
        if (r.IsEmpty) return 0;
        if (_isAlias) throw new InvalidOperationException("Cannot Prepare an alias region");

        // Memory-backed image: alias the existing whole-image buffer with
        // (0,0)-origin and full image stride. No allocation, no GenerateFn.
        // Direct port of libvips vips_region_image (SETBUF/MMAPIN path).
        if (Image.Pixels is { } pixels)
        {
            // Switching from owned → aliased: hand the rented buffer back.
            ReleaseOwnedBuffer();
            _buffer = pixels;
            _bpl = Image.Width * Image.SizeOfPel;
            Valid = r;
            _originX = 0;
            _originY = 0;
            return 0;
        }

        int requiredBpl = r.Width * Image.SizeOfPel;
        int requiredSize = requiredBpl * r.Height;

        // Buffer.Length here is the *pool capacity* — it can exceed
        // requiredSize when the pool returned an oversized array. That's
        // exactly the property we want for the "is the current buffer big
        // enough?" check, so no special handling is needed.
        if (_buffer == null || _buffer.Length < requiredSize)
        {
            ReleaseOwnedBuffer();
            _buffer = Image.Allocator.Rent(requiredSize);
            _ownsBuffer = true;
        }

        _bpl = requiredBpl;
        Valid = r;
        _originX = r.Left;
        _originY = r.Top;

        if (Image.GenerateFn != null)
        {
            // Prepare runs GenerateFn synchronously on the calling thread.
            // Parallelism comes from VipsSink at the consumer end of the
            // pipeline — N workers each drive Prepare on their own region.
            if (!_seqStarted)
            {
                _seq = Image.StartFn?.Invoke(Image, Image.ClientA, Image.ClientB);
                _seqStarted = true;
            }

            bool stop = false;
            Image.GenerateFn(this, _seq, Image.ClientA, Image.ClientB, ref stop);
        }

        return 0;
    }

    public void Dispose()
    {
        if (_seqStarted)
        {
            if (Image.StopFn != null)
                Image.StopFn(_seq, Image.ClientA, Image.ClientB);
            else
                (_seq as IDisposable)?.Dispose();

            _seq = null;
            _seqStarted = false;
        }
        ReleaseOwnedBuffer();
    }

    private void ReleaseOwnedBuffer()
    {
        if (_ownsBuffer && _buffer != null)
        {
            Image.Allocator.Return(_buffer);
            _buffer = null;
            _ownsBuffer = false;
        }
    }
}
