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
/// Round 192 — NIfTI qform/sform metadata round-trip. The loader
/// already exposes spatial-orientation fields (qform_code,
/// quaternion params, sform srow matrices) as
/// <c>image.Metadata["nifti:..."]</c> entries; this round makes the
/// saver write them back when present, so a load → save → load loop
/// preserves the spatial transform exactly.
///
/// <para>We don't apply the transform — VipsImage has no
/// world-coordinate model. The metadata is pass-through for tools
/// downstream that do.</para>
/// </summary>
public class Round192Tests
{
    private const int HeaderSize = 348;

    /// <summary>Build a NIfTI .nii (single-file) with explicit qform / sform fields.</summary>
    private static byte[] BuildNiftiWithSpatialTransforms(
        short qformCode, float qb, float qc, float qd, float qx, float qy, float qz,
        short sformCode, float[] srowX, float[] srowY, float[] srowZ)
    {
        const int VoxOffset = 352;
        // 2×2 uint8 image, all-zero pixels.
        var bytes = new byte[VoxOffset + 4];

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), HeaderSize);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(40, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(42, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(70, 2), 2); // datatype = uint8
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(72, 2), 8); // bitpix
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(80, 4), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(84, 4), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(108, 4), VoxOffset);

        // qform / sform fields.
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(252, 2), qformCode);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(254, 2), sformCode);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(256, 4), qb);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(260, 4), qc);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(264, 4), qd);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(268, 4), qx);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(272, 4), qy);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(276, 4), qz);
        for (int col = 0; col < 4; col++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(280 + col * 4, 4), srowX[col]);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(296 + col * 4, 4), srowY[col]);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(312 + col * 4, 4), srowZ[col]);
        }

        // Magic "n+1\0".
        bytes[344] = (byte)'n'; bytes[345] = (byte)'+'; bytes[346] = (byte)'1'; bytes[347] = 0;
        return bytes;
    }

    private static async Task<VipsImage> LoadAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var img = await VipsNiftiLoader.LoadAsync(src);
        Assert.NotNull(img);
        return img!;
    }

    private static async Task<byte[]> SaveAsync(VipsImage img)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsNiftiSaver.SaveAsync(img, writer);
        return ms.ToArray();
    }

    [Fact]
    public async Task Loader_SurfacesQformAndSformAsMetadata()
    {
        var nifti = BuildNiftiWithSpatialTransforms(
            qformCode: 1, qb: 0.1f, qc: 0.2f, qd: 0.3f, qx: 10f, qy: 20f, qz: 30f,
            sformCode: 2,
            srowX: new[] { 1.0f, 0.0f, 0.0f, 0.0f },
            srowY: new[] { 0.0f, 1.0f, 0.0f, 0.0f },
            srowZ: new[] { 0.0f, 0.0f, 1.0f, 0.0f });
        var img = await LoadAsync(nifti);

        Assert.Equal("1", img.Metadata["nifti:qform_code"]);
        Assert.Equal("2", img.Metadata["nifti:sform_code"]);
        Assert.Equal("0.1,0.2,0.3", img.Metadata["nifti:quatern"]);
        Assert.Equal("10,20,30", img.Metadata["nifti:qoffset"]);
        Assert.Equal("1,0,0,0", img.Metadata["nifti:srow_x"]);
        Assert.Equal("0,1,0,0", img.Metadata["nifti:srow_y"]);
        Assert.Equal("0,0,1,0", img.Metadata["nifti:srow_z"]);
    }

    [Fact]
    public async Task LoadSaveLoad_PreservesSpatialTransforms()
    {
        var original = BuildNiftiWithSpatialTransforms(
            qformCode: 4, qb: 0.5f, qc: -0.5f, qd: 0.7071f, qx: -100f, qy: 50.5f, qz: 25f,
            sformCode: 3,
            srowX: new[] { 2.0f, 0.5f, 0.0f, -10.0f },
            srowY: new[] { 0.5f, 2.0f, 0.0f, 5.0f },
            srowZ: new[] { 0.0f, 0.0f, 1.5f, 100.0f });

        var firstLoad = await LoadAsync(original);
        var resaved = await SaveAsync(firstLoad);
        var secondLoad = await LoadAsync(resaved);

        // Spatial-orientation metadata must survive the round trip exactly.
        Assert.Equal(firstLoad.Metadata["nifti:qform_code"], secondLoad.Metadata["nifti:qform_code"]);
        Assert.Equal(firstLoad.Metadata["nifti:sform_code"], secondLoad.Metadata["nifti:sform_code"]);
        Assert.Equal(firstLoad.Metadata["nifti:quatern"], secondLoad.Metadata["nifti:quatern"]);
        Assert.Equal(firstLoad.Metadata["nifti:qoffset"], secondLoad.Metadata["nifti:qoffset"]);
        Assert.Equal(firstLoad.Metadata["nifti:srow_x"], secondLoad.Metadata["nifti:srow_x"]);
        Assert.Equal(firstLoad.Metadata["nifti:srow_y"], secondLoad.Metadata["nifti:srow_y"]);
        Assert.Equal(firstLoad.Metadata["nifti:srow_z"], secondLoad.Metadata["nifti:srow_z"]);
    }

    [Fact]
    public async Task Loader_Code0_OmitsMetadata()
    {
        // qform_code = 0 means "no transform" — loader should skip the
        // associated quatern/qoffset entries entirely.
        var nifti = BuildNiftiWithSpatialTransforms(
            qformCode: 0, qb: 999f, qc: 999f, qd: 999f, qx: 999f, qy: 999f, qz: 999f,
            sformCode: 0,
            srowX: new float[4], srowY: new float[4], srowZ: new float[4]);
        var img = await LoadAsync(nifti);

        Assert.False(img.Metadata.ContainsKey("nifti:qform_code"));
        Assert.False(img.Metadata.ContainsKey("nifti:quatern"));
        Assert.False(img.Metadata.ContainsKey("nifti:qoffset"));
        Assert.False(img.Metadata.ContainsKey("nifti:sform_code"));
        Assert.False(img.Metadata.ContainsKey("nifti:srow_x"));
    }

    [Fact]
    public async Task Saver_NoMetadata_WritesZeroCodes()
    {
        // Synthesise an image with no nifti:* metadata. Saver must
        // emit qform_code = sform_code = 0 (== "unknown, use pixdim").
        var img = new VipsImage
        {
            Width = 2, Height = 2, Bands = 1, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.BW,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => 0,
        };
        var saved = await SaveAsync(img);
        short qformCode = BinaryPrimitives.ReadInt16LittleEndian(saved.AsSpan(252, 2));
        short sformCode = BinaryPrimitives.ReadInt16LittleEndian(saved.AsSpan(254, 2));
        Assert.Equal(0, qformCode);
        Assert.Equal(0, sformCode);
    }
}
