using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

public class NiftiTests
{
    private static IVipsSource SourceFromBytes(byte[] bytes)
        => new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));

    private static async Task<byte[]> SaveToBytesAsync(System.Func<PipeWriter, Task> save)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        await save(writer);
        return ms.ToArray();
    }

    private static VipsImage UCharGray(int w, int h, byte value)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = value;
                }
                return 0;
            }
        };

    private static VipsImage FloatGray(int w, int h, System.Func<int, int, float> fill)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(
                            addr.Slice(x * 4, 4),
                            fill(reg.Valid.Left + x, reg.Valid.Top + y));
                    }
                }
                return 0;
            }
        };

    private static float ReadFloat(VipsRegion reg, int x, int y, int bnd, int bands)
        => BinaryPrimitives.ReadSingleLittleEndian(reg.GetAddress(x, y).Slice(bnd * 4, 4));

    [Fact]
    public async Task IsNifti_DetectsSinglefileMagic()
    {
        var bytes = await SaveToBytesAsync(w => VipsNiftiSaver.SaveAsync(UCharGray(2, 2, 100), w));
        Assert.True(await VipsNiftiLoader.IsNiftiAsync(SourceFromBytes(bytes)));
    }

    [Fact]
    public async Task IsNifti_RejectsNonNifti()
    {
        var notNifti = new byte[400];
        for (int i = 0; i < notNifti.Length; i++) notNifti[i] = (byte)i;
        Assert.False(await VipsNiftiLoader.IsNiftiAsync(SourceFromBytes(notNifti)));
    }

    [Fact]
    public async Task RoundTrip_2D_UCharGrayscale_PreservesPixels()
    {
        var src = UCharGray(8, 6, value: 175);
        var bytes = await SaveToBytesAsync(w => VipsNiftiSaver.SaveAsync(src, w));

        var loaded = await VipsNiftiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(8, loaded!.Width);
        Assert.Equal(6, loaded.Height);
        Assert.Equal(1, loaded.Bands);
        Assert.Equal(VipsBandFormat.UChar, loaded.BandFormat);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 8, 6));
        Assert.Equal(175, reg.GetAddress(3, 3)[0]);
    }

    [Fact]
    public async Task RoundTrip_2D_Float_PreservesValues()
    {
        var src = FloatGray(4, 3, (x, y) => 0.25f * x + 0.5f * y + 1.0f);
        var bytes = await SaveToBytesAsync(w => VipsNiftiSaver.SaveAsync(src, w));

        var loaded = await VipsNiftiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(VipsBandFormat.Float, loaded!.BandFormat);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 3));
        Assert.Equal(0.25f * 2 + 0.5f * 1 + 1.0f, ReadFloat(reg, 2, 1, 0, 1), 1e-6f);
    }

    [Fact]
    public async Task RoundTrip_3D_PlanarTransposeIsCorrect()
    {
        // 3-band image with distinct per-band fills — planar/interleaved
        // confusion would land wrong values in the wrong band.
        var src = new VipsImage
        {
            Width = 4, Height = 3, Bands = 3, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(addr.Slice((x * 3) * 4, 4), 1.0f);
                        BinaryPrimitives.WriteSingleLittleEndian(addr.Slice((x * 3 + 1) * 4, 4), 2.0f);
                        BinaryPrimitives.WriteSingleLittleEndian(addr.Slice((x * 3 + 2) * 4, 4), 3.0f);
                    }
                }
                return 0;
            }
        };

        var bytes = await SaveToBytesAsync(w => VipsNiftiSaver.SaveAsync(src, w));
        var loaded = await VipsNiftiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Bands);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 3));
        Assert.Equal(1.0f, ReadFloat(reg, 1, 1, 0, 3));
        Assert.Equal(2.0f, ReadFloat(reg, 1, 1, 1, 3));
        Assert.Equal(3.0f, ReadFloat(reg, 1, 1, 2, 3));
    }

    [Fact]
    public async Task PixdimRoundTrip_SetsXResYRes()
    {
        // Voxel size 0.5 mm → XRes/YRes = 2 px/mm.
        var src = UCharGray(4, 4, value: 100);
        src.XRes = 2.0;
        src.YRes = 2.0;
        var bytes = await SaveToBytesAsync(w => VipsNiftiSaver.SaveAsync(src, w));
        var loaded = await VipsNiftiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(2.0, loaded!.XRes, 1e-3);
        Assert.Equal(2.0, loaded.YRes, 1e-3);
    }

    [Fact]
    public async Task SclSlope_Inter_OnLoad_AppliesLinearTransform()
    {
        // Hand-craft a NIfTI with datatype=2 (uint8), scl_slope=2,
        // scl_inter=10. A stored sample of 5 should decode to 5*2 + 10 = 20
        // and the band format should auto-promote to Float because of the
        // value transform.
        var bytes = new byte[352 + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 348);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(40, 2), 2); // ndim = 2
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(42, 2), 4); // nx
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44, 2), 1); // ny
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(46, 2), 1); // nz unused
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(70, 2), 2); // datatype = uint8
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(72, 2), 8); // bitpix
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(80, 4), 1f); // pixdim[1]
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(84, 4), 1f); // pixdim[2]
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(108, 4), 352f); // vox_offset
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(112, 4), 2f); // scl_slope
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(116, 4), 10f); // scl_inter
        bytes[344] = (byte)'n'; bytes[345] = (byte)'+'; bytes[346] = (byte)'1'; bytes[347] = 0;
        // Pixel data at offset 352
        bytes[352] = 5; bytes[353] = 7; bytes[354] = 0; bytes[355] = 100;

        var loaded = await VipsNiftiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(VipsBandFormat.Float, loaded!.BandFormat); // promoted
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        Assert.Equal(20.0f, ReadFloat(reg, 0, 0, 0, 1), 1e-6f); // 5*2+10
        Assert.Equal(24.0f, ReadFloat(reg, 1, 0, 0, 1), 1e-6f); // 7*2+10
        Assert.Equal(10.0f, ReadFloat(reg, 2, 0, 0, 1), 1e-6f); // 0*2+10
        Assert.Equal(210.0f, ReadFloat(reg, 3, 0, 0, 1), 1e-6f); // 100*2+10
    }

    [Fact]
    public async Task BigEndianHeader_AutoDetectedAndDecoded()
    {
        // Hand-craft a big-endian NIfTI: dim[0]=2 stored big-endian. The
        // auto-detect sees the LE read produces a value > 7 and switches
        // to BE for the entire header.
        var bytes = new byte[352 + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), 348);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(40, 2), 2);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(42, 2), 4);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(44, 2), 1);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(46, 2), 1);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(70, 2), 2); // uint8
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(72, 2), 8);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(80, 4), 1f);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(84, 4), 1f);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(108, 4), 352f);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(112, 4), 1f);
        BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(116, 4), 0f);
        bytes[344] = (byte)'n'; bytes[345] = (byte)'+'; bytes[346] = (byte)'1'; bytes[347] = 0;
        bytes[352] = 1; bytes[353] = 2; bytes[354] = 3; bytes[355] = 4;

        var loaded = await VipsNiftiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Width);
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 1));
        Assert.Equal(1, reg.GetAddress(0, 0)[0]);
        Assert.Equal(4, reg.GetAddress(3, 0)[0]);
    }

    [Fact]
    public async Task DescripField_RoundTrips()
    {
        var src = UCharGray(2, 2, 100);
        src.Metadata["nifti:descrip"] = "subject 042 acquired 2024-08";
        var bytes = await SaveToBytesAsync(w => VipsNiftiSaver.SaveAsync(src, w));

        var loaded = await VipsNiftiLoader.LoadAsync(SourceFromBytes(bytes));
        Assert.NotNull(loaded);
        Assert.Equal("subject 042 acquired 2024-08", loaded!.Metadata["nifti:descrip"]);
    }

    [Fact]
    public async Task LoadPaired_2D_Uint8_DecodesFromSeparateFiles()
    {
        // Hand-craft a paired .hdr / .img: ni1 magic in header, vox_offset
        // points into the .img (set to 0 for the typical case).
        var hdr = new byte[348];
        BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(0, 4), 348);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(40, 2), 2); // ndim = 2
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(42, 2), 4); // nx
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(44, 2), 3); // ny
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(46, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(70, 2), 2); // datatype = uint8
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(72, 2), 8);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(80, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(84, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(108, 4), 0f); // vox_offset = 0
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(112, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(116, 4), 0f);
        // Magic ni1 at offset 344.
        hdr[344] = (byte)'n'; hdr[345] = (byte)'i'; hdr[346] = (byte)'1'; hdr[347] = 0;

        // Pixel file: 4×3 uint8 with values 0..11 row-major.
        var img = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        var loaded = await VipsNiftiLoader.LoadPairedAsync(SourceFromBytes(hdr), SourceFromBytes(img));
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Width);
        Assert.Equal(3, loaded.Height);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 3));
        Assert.Equal(0, reg.GetAddress(0, 0)[0]);
        Assert.Equal(3, reg.GetAddress(3, 0)[0]);
        Assert.Equal(11, reg.GetAddress(3, 2)[0]);
    }

    [Fact]
    public async Task LoadPaired_RejectsHeaderWithSinglefileMagic()
    {
        // n+1 magic should be rejected by the paired entry point — that
        // file form is for LoadAsync only.
        var src = UCharGray(2, 2, 100);
        var bytes = await SaveToBytesAsync(w => VipsNiftiSaver.SaveAsync(src, w));
        // Strip the saver's pad+pixels so we only pass the 348-byte header.
        var header = bytes.AsSpan(0, 348).ToArray();
        var data = bytes.AsSpan(352).ToArray();

        var result = await VipsNiftiLoader.LoadPairedAsync(SourceFromBytes(header), SourceFromBytes(data));
        Assert.Null(result);
    }

    [Fact]
    public async Task LoadPaired_3D_FloatRgb_PlanarTransposed()
    {
        // Hand-craft an ni1 paired form for a 4×3×3 float RGB image. The
        // pixel data is column-major within plane × planar across
        // dimensions, just like the single-file form.
        var hdr = new byte[348];
        BinaryPrimitives.WriteInt32LittleEndian(hdr.AsSpan(0, 4), 348);
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(40, 2), 3); // ndim = 3
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(42, 2), 4); // nx
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(44, 2), 3); // ny
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(46, 2), 3); // nz = bands
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(70, 2), 16); // float32
        BinaryPrimitives.WriteInt16LittleEndian(hdr.AsSpan(72, 2), 32);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(80, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(84, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(108, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(112, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.AsSpan(116, 4), 0f);
        hdr[344] = (byte)'n'; hdr[345] = (byte)'i'; hdr[346] = (byte)'1'; hdr[347] = 0;

        // 4*3 = 12 pixels per plane × 3 planes × 4 bytes = 144 bytes.
        var img = new byte[144];
        // Plane 0 = constant 1.0, plane 1 = 2.0, plane 2 = 3.0. NIfTI is
        // X-fastest so each plane is laid out row by row with x varying first.
        for (int p = 0; p < 3; p++)
        {
            for (int i = 0; i < 12; i++)
            {
                int off = p * 48 + i * 4;
                BinaryPrimitives.WriteSingleLittleEndian(img.AsSpan(off, 4), p + 1f);
            }
        }

        var loaded = await VipsNiftiLoader.LoadPairedAsync(SourceFromBytes(hdr), SourceFromBytes(img));
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Bands);
        Assert.Equal(VipsBandFormat.Float, loaded.BandFormat);

        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 3));
        Assert.Equal(1.0f, ReadFloat(reg, 1, 1, 0, 3));
        Assert.Equal(2.0f, ReadFloat(reg, 1, 1, 1, 3));
        Assert.Equal(3.0f, ReadFloat(reg, 1, 1, 2, 3));
    }

    [Fact]
    public async Task NiftiPipeline_LoadResizeSave_StaysInFloat()
    {
        var src = FloatGray(8, 8, (x, y) => 100.0f);
        var encoded = await SaveToBytesAsync(w => VipsNiftiSaver.SaveAsync(src, w));

        var loaded = await VipsNiftiLoader.LoadAsync(SourceFromBytes(encoded));
        Assert.NotNull(loaded);
        var resized = loaded!.Resize(0.5);
        var reEncoded = await SaveToBytesAsync(w => VipsNiftiSaver.SaveAsync(resized, w));
        var reloaded = await VipsNiftiLoader.LoadAsync(SourceFromBytes(reEncoded));
        Assert.NotNull(reloaded);
        Assert.Equal(VipsBandFormat.Float, reloaded!.BandFormat);

        using var reg = new VipsRegion(reloaded);
        reg.Prepare(new VipsRect(0, 0, reloaded.Width, reloaded.Height));
        Assert.Equal(100.0f, ReadFloat(reg, 1, 1, 0, 1), 1e-3f);
    }
}
