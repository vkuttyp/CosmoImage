using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 191 — NIfTI paired-form save. Mirrors the existing
/// <see cref="VipsNiftiLoader.LoadPairedAsync"/>: emits a
/// 348-byte <c>.hdr</c> file (magic <c>"ni1\0"</c>) and a
/// separate <c>.img</c> file holding raw pixel data at offset 0.
///
/// <para>Tests round-trip a synthetic image through SavePaired →
/// LoadPaired and verify pixel-exactness across both UChar and
/// Float datatypes.</para>
/// </summary>
public class Round191Tests
{
    private static VipsImage MakeUChar(int w, int h)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                        addr[x] = (byte)(((reg.Valid.Top + y) * 7 + (reg.Valid.Left + x) * 11) & 0xFF);
                }
                return 0;
            }
        };

    private static VipsImage MakeFloat3D(int w, int h, int planes)
        => new VipsImage
        {
            Width = w, Height = h, Bands = planes, BandFormat = VipsBandFormat.Float,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        for (int c = 0; c < planes; c++)
                        {
                            float v = (reg.Valid.Top + y) * 0.1f + (reg.Valid.Left + x) * 0.05f + c * 100f;
                            BinaryPrimitives.WriteSingleLittleEndian(
                                addr.Slice((x * planes + c) * 4, 4), v);
                        }
                    }
                }
                return 0;
            }
        };

    private static async Task<(byte[] hdr, byte[] img)> SavePairedAsync(VipsImage img)
    {
        using var hdrMs = new MemoryStream();
        using var imgMs = new MemoryStream();
        var hdrWriter = PipeWriter.Create(hdrMs);
        var imgWriter = PipeWriter.Create(imgMs);
        await VipsNiftiSaver.SavePairedAsync(img, hdrWriter, imgWriter);
        return (hdrMs.ToArray(), imgMs.ToArray());
    }

    private static async Task<VipsImage> LoadPairedAsync(byte[] hdr, byte[] img)
    {
        var hdrSrc = new PipeVipsSource(PipeReader.Create(new MemoryStream(hdr)));
        var imgSrc = new PipeVipsSource(PipeReader.Create(new MemoryStream(img)));
        var loaded = await VipsNiftiLoader.LoadPairedAsync(hdrSrc, imgSrc);
        Assert.NotNull(loaded);
        return loaded!;
    }

    [Fact]
    public async Task Hdr_HasNi1MagicAndIs348Bytes()
    {
        var src = MakeUChar(4, 4);
        var (hdr, img) = await SavePairedAsync(src);

        // Paired .hdr is exactly 348 bytes — no 4-byte pad like .nii.
        Assert.Equal(348, hdr.Length);
        // Magic at offset 344: "ni1\0".
        Assert.Equal((byte)'n', hdr[344]);
        Assert.Equal((byte)'i', hdr[345]);
        Assert.Equal((byte)'1', hdr[346]);
        Assert.Equal((byte)0, hdr[347]);
        // vox_offset (offset 108) must be 0 for paired form.
        float voxOffset = BinaryPrimitives.ReadSingleLittleEndian(hdr.AsSpan(108, 4));
        Assert.Equal(0f, voxOffset);
        // .img starts immediately at byte 0; no leading pad.
        Assert.Equal(16, img.Length); // 4×4×1 byte
    }

    [Fact]
    public async Task UChar2D_RoundTripsExactly()
    {
        var src = MakeUChar(8, 6);
        var (hdr, imgData) = await SavePairedAsync(src);
        var loaded = await LoadPairedAsync(hdr, imgData);

        Assert.Equal(8, loaded.Width);
        Assert.Equal(6, loaded.Height);
        Assert.Equal(VipsBandFormat.UChar, loaded.BandFormat);
        Assert.Equal(1, loaded.Bands);

        using var srcReg = new VipsRegion(src);
        using var loadedReg = new VipsRegion(loaded);
        srcReg.Prepare(new VipsRect(0, 0, 8, 6));
        loadedReg.Prepare(new VipsRect(0, 0, 8, 6));

        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal(srcReg.GetAddress(x, y)[0], loadedReg.GetAddress(x, y)[0]);
    }

    [Fact]
    public async Task Float3D_RoundTripsExactly()
    {
        var src = MakeFloat3D(4, 3, 2);
        var (hdr, imgData) = await SavePairedAsync(src);
        var loaded = await LoadPairedAsync(hdr, imgData);

        Assert.Equal(4, loaded.Width);
        Assert.Equal(3, loaded.Height);
        Assert.Equal(VipsBandFormat.Float, loaded.BandFormat);
        Assert.Equal(2, loaded.Bands);

        using var srcReg = new VipsRegion(src);
        using var loadedReg = new VipsRegion(loaded);
        srcReg.Prepare(new VipsRect(0, 0, 4, 3));
        loadedReg.Prepare(new VipsRect(0, 0, 4, 3));

        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
                for (int c = 0; c < 2; c++)
                {
                    float a = BinaryPrimitives.ReadSingleLittleEndian(
                        srcReg.GetAddress(x, y).Slice((c) * 4, 4));
                    float b = BinaryPrimitives.ReadSingleLittleEndian(
                        loadedReg.GetAddress(x, y).Slice((c) * 4, 4));
                    Assert.Equal(a, b);
                }
    }

    [Fact]
    public async Task DescriptionMetadata_RoundTrips()
    {
        var src = MakeUChar(4, 4);
        src.Metadata["nifti:descrip"] = "round-191-paired-form-test";
        var (hdr, imgData) = await SavePairedAsync(src);
        var loaded = await LoadPairedAsync(hdr, imgData);

        Assert.True(loaded.Metadata.TryGetValue("nifti:descrip", out var descrip));
        Assert.Equal("round-191-paired-form-test", descrip);
    }
}
