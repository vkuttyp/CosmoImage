using System;
using System.Runtime.InteropServices;

namespace CosmoImage.Core;

/// <summary>
/// Strongly-typed pixel view backed by a contiguous row-major byte buffer.
/// This is the typed-access layer over <see cref="VipsImage"/> — for direct
/// pixel read/write workflows where compile-time band/format safety matters
/// more than the lazy demand-driven pipeline.
///
/// <para>Construct via:</para>
/// <list type="bullet">
/// <item><description>
/// <c>new TypedImage&lt;Rgba32&gt;(width, height)</c> — fresh writable buffer,
/// every pixel zeroed.
/// </description></item>
/// <item><description>
/// <c>vipsImage.ToTypedImage&lt;Rgba32&gt;()</c> — materializes the lazy
/// pipeline once via <see cref="MemorySink"/>, producing a typed view over
/// the resulting buffer. The typed wrapper owns the buffer afterward, so
/// mutations on the typed side don't affect the source <see cref="VipsImage"/>.
/// </description></item>
/// </list>
///
/// <para>Feed back into the op pipeline with <see cref="AsVipsImage"/>:</para>
/// <code>
/// var t = new TypedImage&lt;Rgba32&gt;(w, h);
/// for (int y = 0; y &lt; h; y++)
///     foreach (ref Rgba32 px in t.RowSpan(y)) px.A = 255;
/// var vips = t.AsVipsImage().Resize(0.5).Sepia();
/// </code>
///
/// Pixel/band/format mismatch is caught at construction and throws —
/// <typeparamref name="TPixel"/> must satisfy
/// <c>TPixel.BandCount == image.Bands</c> and
/// <c>TPixel.BandFormat == image.BandFormat</c>.
/// </summary>
public sealed class TypedImage<TPixel>
    where TPixel : struct, IPixel<TPixel>
{
    private readonly byte[] _buffer;
    private readonly int _stride;

    public int Width { get; }
    public int Height { get; }

    /// <summary>String-keyed parsed metadata, mirrors <see cref="VipsImage.Metadata"/>.</summary>
    public System.Collections.Generic.Dictionary<string, string> Metadata { get; } = new();

    /// <summary>Raw byte-array metadata (EXIF/XMP/ICC), mirrors <see cref="VipsImage.MetadataBlobs"/>.</summary>
    public System.Collections.Generic.Dictionary<string, byte[]> MetadataBlobs { get; } = new();

    /// <summary>Allocate a fresh writable typed buffer of the given dimensions.</summary>
    public TypedImage(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        _stride = width * PixelByteSize();
        _buffer = new byte[_stride * height];
    }

    /// <summary>
    /// Materialize <paramref name="source"/> through <see cref="MemorySink"/>
    /// and wrap the resulting buffer for typed access. Throws when the source's
    /// runtime band/format don't match <typeparamref name="TPixel"/>.
    /// </summary>
    public TypedImage(VipsImage source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        ValidatePixelMatch(source.Bands, source.BandFormat);

        Width = source.Width;
        Height = source.Height;
        _stride = source.Width * PixelByteSize();

        if (source.Pixels is { } existing)
        {
            // Already memory-backed — copy so the typed view owns its buffer
            // and mutations don't ripple into the shared source. Same cost
            // as MemorySink would pay anyway.
            _buffer = new byte[existing.Length];
            Buffer.BlockCopy(existing, 0, _buffer, 0, existing.Length);
        }
        else
        {
            var sink = new MemorySink(source);
            sink.RunAsync().GetAwaiter().GetResult();
            _buffer = sink.Pixels;
        }

        foreach (var kv in source.Metadata) Metadata[kv.Key] = kv.Value;
        foreach (var kv in source.MetadataBlobs) MetadataBlobs[kv.Key] = kv.Value;
    }

    /// <summary>Read or overwrite the pixel at <c>(x, y)</c>. Bounds-checked.</summary>
    public TPixel this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
            return MemoryMarshal.Read<TPixel>(_buffer.AsSpan(y * _stride + x * PixelByteSize()));
        }
        set
        {
            if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
            MemoryMarshal.Write(_buffer.AsSpan(y * _stride + x * PixelByteSize()), in value);
        }
    }

    /// <summary>
    /// A typed span over row <paramref name="y"/>. Use this for tight loops:
    /// <c>foreach (ref TPixel px in image.RowSpan(y)) ...</c>. Reinterprets the
    /// underlying byte buffer with no copy via <see cref="MemoryMarshal.Cast{TFrom, TTo}(System.Span{TFrom})"/>.
    /// </summary>
    public Span<TPixel> RowSpan(int y)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return MemoryMarshal.Cast<byte, TPixel>(_buffer.AsSpan(y * _stride, _stride));
    }

    /// <summary>
    /// Expose this typed buffer as a memory-backed <see cref="VipsImage"/> so
    /// it can feed the lazy op pipeline. The returned image aliases the same
    /// byte buffer — subsequent mutations through <see cref="this[int, int]"/>
    /// or <see cref="RowSpan"/> are visible to ops that haven't yet been
    /// materialized.
    /// </summary>
    public VipsImage AsVipsImage()
    {
        var bufferRef = _buffer;
        var img = new VipsImage
        {
            Width = Width,
            Height = Height,
            Bands = TPixel.BandCount,
            BandFormat = TPixel.BandFormat,
            Interpretation = TPixel.BandCount switch
            {
                1 or 2 => VipsInterpretation.BW,
                _ => VipsInterpretation.RGB,
            },
            Coding = VipsCoding.None,
            XRes = 1.0,
            YRes = 1.0,
            PixelsLazy = new Lazy<byte[]>(() => bufferRef),
        };
        foreach (var kv in Metadata) img.Metadata[kv.Key] = kv.Value;
        foreach (var kv in MetadataBlobs) img.MetadataBlobs[kv.Key] = kv.Value;
        return img;
    }

    private static int PixelByteSize()
    {
        // BandCount × bytes-per-band. For UChar that's just BandCount; the
        // multiply form holds when Float/Short variants land later.
        return TPixel.BandCount * VipsEnumsExtensions.SizeOf(TPixel.BandFormat);
    }

    private static void ValidatePixelMatch(int bands, VipsBandFormat format)
    {
        if (bands != TPixel.BandCount)
            throw new InvalidOperationException(
                $"Pixel type {typeof(TPixel).Name} expects {TPixel.BandCount} band(s) but image has {bands}.");
        if (format != TPixel.BandFormat)
            throw new InvalidOperationException(
                $"Pixel type {typeof(TPixel).Name} expects band format {TPixel.BandFormat} but image has {format}.");
    }
}
