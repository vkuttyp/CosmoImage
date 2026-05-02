using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Coverage for the pure-C# Matlab v5 .mat numeric-array reader. We
/// hand-craft minimal .mat files at test time — Magick.NET doesn't
/// support .mat write, so synthetic fixtures are the only realistic
/// option in-test. The format is well-defined enough that this is
/// straightforward.
/// </summary>
public class MatLoaderTests
{
    private const uint MiInt8 = 1;
    private const uint MiUInt8 = 2;
    private const uint MiInt32 = 5;
    private const uint MiUInt32 = 6;
    private const uint MiDouble = 9;
    private const uint MiMatrix = 14;
    private const uint MiCompressed = 15;

    private const byte MxDouble = 6;
    private const byte MxUInt8 = 9;

    private static IVipsSource SourceFromBytes(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    /// <summary>
    /// Build a minimal v5 .mat header (128 bytes) followed by the supplied
    /// element bytes. Output is little-endian throughout.
    /// </summary>
    private static byte[] BuildMatFile(byte[] elements, bool littleEndian = true)
    {
        var ms = new MemoryStream();
        // 116 bytes of descriptor — any printable ASCII works.
        var descriptor = new byte[116];
        var descriptorText = System.Text.Encoding.ASCII.GetBytes(
            "MATLAB 5.0 MAT-file, Created by CosmoImage tests.");
        Buffer.BlockCopy(descriptorText, 0, descriptor, 0, descriptorText.Length);
        // Pad with spaces (Matlab's convention) so the version field reads
        // cleanly even if a tool inspects the header.
        for (int i = descriptorText.Length; i < 116; i++) descriptor[i] = (byte)' ';
        ms.Write(descriptor);

        // 8 bytes subsystem-specific data offset (zero for "none").
        ms.Write(new byte[8]);

        // 2 bytes version (0x0100), 2 bytes endian marker.
        var verBytes = new byte[2];
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(verBytes, 0x0100);
        else BinaryPrimitives.WriteUInt16BigEndian(verBytes, 0x0100);
        ms.Write(verBytes);
        if (littleEndian) ms.Write(new byte[] { (byte)'I', (byte)'M' });
        else ms.Write(new byte[] { (byte)'M', (byte)'I' });

        ms.Write(elements);
        return ms.ToArray();
    }

    /// <summary>
    /// Build a regular-form (non-small) v5 element: 8-byte tag + data,
    /// padded to 8-byte boundary.
    /// </summary>
    private static byte[] Element(uint type, byte[] data, bool littleEndian = true)
    {
        var ms = new MemoryStream();
        var typeBytes = new byte[4];
        var lenBytes = new byte[4];
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(typeBytes, type);
            BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)data.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(typeBytes, type);
            BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        }
        ms.Write(typeBytes);
        ms.Write(lenBytes);
        ms.Write(data);
        // Pad to 8-byte boundary.
        int total = 8 + data.Length;
        int pad = ((total + 7) & ~7) - total;
        if (pad > 0) ms.Write(new byte[pad]);
        return ms.ToArray();
    }

    /// <summary>
    /// Build a miMATRIX element body for a 2D or 3D numeric array.
    /// <paramref name="data"/> is the column-major raw pixel bytes (the
    /// matlab convention) for the Real Part sub-element.
    /// </summary>
    private static byte[] BuildMatrix(int H, int W, int planes, byte cls, uint dataType, byte[] data, string name = "img")
    {
        var ms = new MemoryStream();

        // (1) Array Flags: miUINT32, 8 bytes data. First u32 = (flags<<8) | class.
        var flags = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(flags.AsSpan(0, 4), cls);
        // bytes 4..7 = nzmax (zero for non-sparse)
        ms.Write(Element(MiUInt32, flags));

        // (2) Dimensions: miINT32, ndim × 4 bytes
        int ndim = planes == 1 ? 2 : 3;
        var dims = new byte[ndim * 4];
        BinaryPrimitives.WriteInt32LittleEndian(dims.AsSpan(0, 4), H);
        BinaryPrimitives.WriteInt32LittleEndian(dims.AsSpan(4, 4), W);
        if (planes != 1) BinaryPrimitives.WriteInt32LittleEndian(dims.AsSpan(8, 4), planes);
        ms.Write(Element(MiInt32, dims));

        // (3) Array Name: miINT8, padded
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        ms.Write(Element(MiInt8, nameBytes));

        // (4) Real Part: numeric type
        ms.Write(Element(dataType, data));
        return ms.ToArray();
    }

    [Fact]
    public async Task IsMat_DetectsLittleEndianHeader()
    {
        var matrix = BuildMatrix(2, 2, 1, MxUInt8, MiUInt8, new byte[] { 10, 20, 30, 40 });
        var elem = Element(MiMatrix, matrix);
        var file = BuildMatFile(elem);
        Assert.True(await VipsMatLoader.IsMatAsync(SourceFromBytes(file)));
    }

    [Fact]
    public async Task IsMat_RejectsNonMat()
    {
        var notMat = new byte[200];
        for (int i = 0; i < notMat.Length; i++) notMat[i] = (byte)(i & 0xFF);
        Assert.False(await VipsMatLoader.IsMatAsync(SourceFromBytes(notMat)));
    }

    [Fact]
    public async Task Load_2D_Uint8_TransposesColumnMajorToRowMajor()
    {
        // Source matrix (matlab notation, 1-indexed):
        //   [1  4]
        //   [2  5]
        //   [3  6]
        // dims = [3, 2]. Column-major storage: 1, 2, 3, 4, 5, 6.
        // After load (row-major VipsImage):
        //   row 0: 1, 4
        //   row 1: 2, 5
        //   row 2: 3, 6
        var data = new byte[] { 1, 2, 3, 4, 5, 6 };
        var matrix = BuildMatrix(H: 3, W: 2, planes: 1, MxUInt8, MiUInt8, data);
        var file = BuildMatFile(Element(MiMatrix, matrix));

        var img = await VipsMatLoader.LoadAsync(SourceFromBytes(file));
        Assert.NotNull(img);
        Assert.Equal(2, img!.Width);
        Assert.Equal(3, img.Height);
        Assert.Equal(1, img.Bands);
        Assert.Equal(VipsBandFormat.UChar, img.BandFormat);

        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 2, 3));
        Assert.Equal(1, reg.GetAddress(0, 0)[0]);
        Assert.Equal(4, reg.GetAddress(1, 0)[0]);
        Assert.Equal(3, reg.GetAddress(0, 2)[0]);
        Assert.Equal(6, reg.GetAddress(1, 2)[0]);
    }

    [Fact]
    public async Task Load_2D_Double_PromotesToFloat()
    {
        // 2x2 doubles: [1.5, 3.5; 2.5, 4.5] (matlab; col-major: 1.5, 2.5, 3.5, 4.5).
        var data = new byte[4 * 8];
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(0, 8), 1.5);
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(8, 8), 2.5);
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(16, 8), 3.5);
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(24, 8), 4.5);

        var matrix = BuildMatrix(H: 2, W: 2, planes: 1, MxDouble, MiDouble, data);
        var file = BuildMatFile(Element(MiMatrix, matrix));

        var img = await VipsMatLoader.LoadAsync(SourceFromBytes(file));
        Assert.NotNull(img);
        Assert.Equal(VipsBandFormat.Float, img!.BandFormat);

        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 0).Slice(0, 4)));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(0, 1).Slice(0, 4)));
        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(1, 0).Slice(0, 4)));
        Assert.Equal(4.5f, BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(1, 1).Slice(0, 4)));
    }

    [Fact]
    public async Task Load_3D_Uint8_MultiBandPlanarLayout()
    {
        // 2x2x3 uint8 (matlab convention dims = [H, W, C]). Column-major
        // within each plane, planes laid out one after another.
        // Plane 0 (R): [10 20; 30 40] → col-major: 10, 30, 20, 40
        // Plane 1 (G): [50 60; 70 80] → col-major: 50, 70, 60, 80
        // Plane 2 (B): [90 100; 110 120] → col-major: 90, 110, 100, 120
        var data = new byte[]
        {
            10, 30, 20, 40,
            50, 70, 60, 80,
            90, 110, 100, 120,
        };
        var matrix = BuildMatrix(H: 2, W: 2, planes: 3, MxUInt8, MiUInt8, data);
        var file = BuildMatFile(Element(MiMatrix, matrix));

        var img = await VipsMatLoader.LoadAsync(SourceFromBytes(file));
        Assert.NotNull(img);
        Assert.Equal(3, img!.Bands);

        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 2, 2));
        // Pixel (0, 0) — top-left — has R=10, G=50, B=90.
        Assert.Equal(10, reg.GetAddress(0, 0)[0]);
        Assert.Equal(50, reg.GetAddress(0, 0)[1]);
        Assert.Equal(90, reg.GetAddress(0, 0)[2]);
        // Pixel (1, 1) — bottom-right — has R=40, G=80, B=120.
        Assert.Equal(40, reg.GetAddress(1, 1)[0]);
        Assert.Equal(80, reg.GetAddress(1, 1)[1]);
        Assert.Equal(120, reg.GetAddress(1, 1)[2]);
    }

    [Fact]
    public async Task Load_CompressedMatrix_Inflates()
    {
        // Same content as Load_2D_Uint8 but wrapped in a miCOMPRESSED
        // element. Confirms the zlib inflate path runs.
        var matrix = BuildMatrix(H: 3, W: 2, planes: 1, MxUInt8, MiUInt8, new byte[] { 1, 2, 3, 4, 5, 6 });
        var inner = Element(MiMatrix, matrix);

        // Compress with zlib (RFC 1950 wrapper).
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(inner, 0, inner.Length);
        }
        var compElement = Element(MiCompressed, compressed.ToArray());
        var file = BuildMatFile(compElement);

        var img = await VipsMatLoader.LoadAsync(SourceFromBytes(file));
        Assert.NotNull(img);
        Assert.Equal(2, img!.Width);
        Assert.Equal(3, img.Height);

        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, 2, 3));
        Assert.Equal(1, reg.GetAddress(0, 0)[0]);
        Assert.Equal(6, reg.GetAddress(1, 2)[0]);
    }

    [Fact]
    public async Task Load_Pipeline_RoundsThroughResize()
    {
        // Confirm a loaded .mat plays nicely with the lazy op pipeline.
        var data = new byte[16 * 16];
        for (int i = 0; i < data.Length; i++) data[i] = 100;
        var matrix = BuildMatrix(H: 16, W: 16, planes: 1, MxUInt8, MiUInt8, data);
        var file = BuildMatFile(Element(MiMatrix, matrix));

        var img = await VipsMatLoader.LoadAsync(SourceFromBytes(file));
        Assert.NotNull(img);
        var smaller = img!.Resize(0.5);
        Assert.Equal(8, smaller.Width);
        Assert.Equal(8, smaller.Height);

        using var reg = new VipsRegion(smaller);
        reg.Prepare(new VipsRect(0, 0, 8, 8));
        Assert.Equal(100, reg.GetAddress(4, 4)[0]);
    }
}
