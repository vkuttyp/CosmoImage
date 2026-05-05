using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Core;
using CosmoImage.Loaders;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 190 — NIfTI integer datatypes (int8 / int16 / int32 /
/// uint16 / uint32). Previously the loader bailed on anything
/// outside { uint8, float32, float64 } — common in raw scanner data.
/// All integer datatypes now widen to Float on output (lossless
/// preservation of the full range).
///
/// <para>Each test crafts a minimal NIfTI-1 single-file (.nii)
/// header + pixel block by hand and pushes it through the loader,
/// pinning a few specific pixel values that exercise the
/// signed/unsigned branch.</para>
/// </summary>
public class Round190Tests
{
    private const int HeaderSize = 348;

    /// <summary>
    /// Build a minimal NIfTI-1 single-file header for a 2D image of
    /// the given datatype. Pixel data appears immediately after a
    /// 4-byte gap (vox_offset = 352 per spec).
    /// </summary>
    private static byte[] BuildNifti(int width, int height, short datatype, short bitpix, byte[] pixelData)
    {
        const int VoxOffset = 352;
        var bytes = new byte[VoxOffset + pixelData.Length];

        // sizeof_hdr (offset 0) = 348.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), HeaderSize);
        // dim_info (offset 39) = 0.
        // dim[0..7] starting at offset 40: dim[0] = 2 for 2D.
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(40, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(42, 2), (short)width);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44, 2), (short)height);
        // datatype (offset 70), bitpix (72).
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(70, 2), datatype);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(72, 2), bitpix);
        // pixdim[0..7] at offset 76. Set pixdim[1] = pixdim[2] = 1.
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(80, 4), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(84, 4), 1.0f);
        // vox_offset (108).
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(108, 4), VoxOffset);
        // scl_slope (112) = 0 means "no scaling" per spec — leave 0.
        // magic (offset 344) = "n+1" + null for single-file format.
        bytes[344] = (byte)'n';
        bytes[345] = (byte)'+';
        bytes[346] = (byte)'1';
        bytes[347] = 0;

        Buffer.BlockCopy(pixelData, 0, bytes, VoxOffset, pixelData.Length);
        return bytes;
    }

    private static async Task<VipsImage> LoadAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var img = await VipsNiftiLoader.LoadAsync(src);
        Assert.NotNull(img);
        return img!;
    }

    private static float ReadFloat(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        var addr = reg.GetAddress(x, y);
        return BinaryPrimitives.ReadSingleLittleEndian(addr.Slice(0, 4));
    }

    [Fact]
    public async Task Int16_NegativeAndPositive_PreservedAsFloat()
    {
        // 2×2 int16 image: values { -32000, -1, 1, 32000 }.
        // Output should be Float with the same numeric values.
        var pixels = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(pixels.AsSpan(0, 2), -32000);
        BinaryPrimitives.WriteInt16LittleEndian(pixels.AsSpan(2, 2), -1);
        BinaryPrimitives.WriteInt16LittleEndian(pixels.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(pixels.AsSpan(6, 2), 32000);

        var nifti = BuildNifti(2, 2, datatype: 4, bitpix: 16, pixels);
        var img = await LoadAsync(nifti);

        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
        Assert.Equal(2, img.Width);
        Assert.Equal(2, img.Height);

        Assert.Equal(-32000f, ReadFloat(img, 0, 0));
        Assert.Equal(-1f, ReadFloat(img, 1, 0));
        Assert.Equal(1f, ReadFloat(img, 0, 1));
        Assert.Equal(32000f, ReadFloat(img, 1, 1));
    }

    [Fact]
    public async Task Int32_LargeRange_PreservedAsFloat()
    {
        // 2×1 int32 image: { INT32_MIN/2, INT32_MAX/2 }.
        // Both fit cleanly in Float (24 bits of precision is enough
        // for half-INT32 range with rounding).
        var pixels = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(0, 4), int.MinValue / 2);
        BinaryPrimitives.WriteInt32LittleEndian(pixels.AsSpan(4, 4), int.MaxValue / 2);

        var nifti = BuildNifti(2, 1, datatype: 8, bitpix: 32, pixels);
        var img = await LoadAsync(nifti);

        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
        Assert.Equal((float)(int.MinValue / 2), ReadFloat(img, 0, 0));
        Assert.Equal((float)(int.MaxValue / 2), ReadFloat(img, 1, 0));
    }

    [Fact]
    public async Task Int8_SignExtensionWorks()
    {
        // 2×1 int8 image: { -128, 127 }.
        // (sbyte) cast preserves sign on the negative value.
        var pixels = new byte[] { 0x80, 0x7F }; // -128, 127 as bytes
        var nifti = BuildNifti(2, 1, datatype: 256, bitpix: 8, pixels);
        var img = await LoadAsync(nifti);

        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
        Assert.Equal(-128f, ReadFloat(img, 0, 0));
        Assert.Equal(127f, ReadFloat(img, 1, 0));
    }

    [Fact]
    public async Task UInt16_FullRangePreserved()
    {
        // 2×1 uint16 image: { 0, 65535 }.
        var pixels = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(2, 2), 65535);

        var nifti = BuildNifti(2, 1, datatype: 512, bitpix: 16, pixels);
        var img = await LoadAsync(nifti);

        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
        Assert.Equal(0f, ReadFloat(img, 0, 0));
        Assert.Equal(65535f, ReadFloat(img, 1, 0));
    }

    [Fact]
    public async Task UInt32_FullRangePreserved()
    {
        // 2×1 uint32 image: { 0, UINT32_MAX/2 }.
        var pixels = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan(4, 4), uint.MaxValue / 2);

        var nifti = BuildNifti(2, 1, datatype: 768, bitpix: 32, pixels);
        var img = await LoadAsync(nifti);

        Assert.Equal(VipsBandFormat.Float, img.BandFormat);
        Assert.Equal(0f, ReadFloat(img, 0, 0));
        Assert.Equal((float)(uint.MaxValue / 2), ReadFloat(img, 1, 0));
    }

    [Fact]
    public async Task UInt8_StaysUCharWithoutTransform()
    {
        // Sanity check: uint8 with no scaling should still emit UChar
        // (regression check that the int-types-go-Float path didn't
        // accidentally promote uint8 too).
        var pixels = new byte[] { 50, 100, 150, 200 };
        var nifti = BuildNifti(2, 2, datatype: 2, bitpix: 8, pixels);
        var img = await LoadAsync(nifti);
        Assert.Equal(VipsBandFormat.UChar, img.BandFormat);
    }
}
