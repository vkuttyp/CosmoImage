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
/// Round 193 — NIfTI 4D + large-Z height-stacked layout. Previously
/// the loader bailed on <c>ndims = 4</c> entirely, and 3D with
/// <c>nz &gt; 4</c>. Both paths now route through a height-stacked
/// single-band layout with <c>n-pages = nz·nt</c>,
/// <c>page-height = ny</c>, and <c>nifti:dim3 / dim4</c> metadata
/// preserving the original axis counts.
///
/// <para>3D with <c>nz ≤ 4</c> still uses the legacy Z-as-bands
/// layout for back-compat — the most common pathology / colour-merged
/// neuroimaging case.</para>
/// </summary>
public class Round193Tests
{
    private const int HeaderSize = 348;

    /// <summary>
    /// Build an N-D NIfTI .nii (single-file) with the given dim sizes.
    /// Ndims is derived from which dims are &gt; 1. Uses uint8 datatype
    /// throughout for simple test fixtures.
    /// </summary>
    private static byte[] BuildNifti(int nx, int ny, int nz, int nt, byte[] pixelBytes)
    {
        const int VoxOffset = 352;
        var bytes = new byte[VoxOffset + pixelBytes.Length];

        // sizeof_hdr at offset 0.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), HeaderSize);

        // ndims is the highest axis with dim > 1 (clamped to ≥ 2).
        int ndims = nt > 1 ? 4 : (nz > 1 ? 3 : 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(40, 2), (short)ndims);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(42, 2), (short)nx);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44, 2), (short)ny);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(46, 2), (short)nz);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(48, 2), (short)nt);

        // datatype = uint8.
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(70, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(72, 2), 8);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(80, 4), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(84, 4), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(108, 4), VoxOffset);

        // Magic "n+1\0".
        bytes[344] = (byte)'n'; bytes[345] = (byte)'+'; bytes[346] = (byte)'1'; bytes[347] = 0;

        Buffer.BlockCopy(pixelBytes, 0, bytes, VoxOffset, pixelBytes.Length);
        return bytes;
    }

    private static async Task<VipsImage> LoadAsync(byte[] bytes)
    {
        var src = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var img = await VipsNiftiLoader.LoadAsync(src);
        Assert.NotNull(img);
        return img!;
    }

    private static byte ReadPel(VipsImage img, int x, int y)
    {
        using var reg = new VipsRegion(img);
        reg.Prepare(new VipsRect(0, 0, img.Width, img.Height));
        return reg.GetAddress(x, y)[0];
    }

    [Fact]
    public async Task Nifti4D_StacksFramesIntoHeight()
    {
        // 3×2×2×3 (X×Y×Z×T): 12 frames total, page-height = 2.
        // Pixel value = frame index (0..11).
        const int nx = 3, ny = 2, nz = 2, nt = 3;
        var pixels = new byte[nx * ny * nz * nt];
        for (int t = 0; t < nt; t++)
            for (int z = 0; z < nz; z++)
            {
                int frame = t * nz + z;
                for (int i = 0; i < nx * ny; i++)
                    pixels[frame * nx * ny + i] = (byte)frame;
            }

        var nifti = BuildNifti(nx, ny, nz, nt, pixels);
        var img = await LoadAsync(nifti);

        // Stacked layout: 1-band, height = ny * nz * nt.
        Assert.Equal(1, img.Bands);
        Assert.Equal(nx, img.Width);
        Assert.Equal(ny * nz * nt, img.Height);

        // Axis-count metadata + animation-style n-pages.
        // n-pages = nz * nt = 6 frames, page-height = ny = 2.
        Assert.Equal("2", img.Metadata["nifti:dim3"]);
        Assert.Equal("3", img.Metadata["nifti:dim4"]);
        Assert.Equal("6", img.Metadata["n-pages"]);
        Assert.Equal("2", img.Metadata["page-height"]);

        // Each frame's height-stacked rows must hold its frame value.
        for (int frame = 0; frame < nz * nt; frame++)
        {
            int yBase = frame * ny;
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    Assert.Equal((byte)frame, ReadPel(img, x, yBase + y));
        }
    }

    [Fact]
    public async Task Nifti3DLargeZ_StacksFramesIntoHeight()
    {
        // 3D with nz = 8 (> 4): legacy Z-as-bands path can't fit, so
        // the loader falls back to height-stacked layout.
        const int nx = 4, ny = 3, nz = 8, nt = 1;
        var pixels = new byte[nx * ny * nz];
        for (int z = 0; z < nz; z++)
            for (int i = 0; i < nx * ny; i++)
                pixels[z * nx * ny + i] = (byte)z;

        var nifti = BuildNifti(nx, ny, nz, nt, pixels);
        var img = await LoadAsync(nifti);

        Assert.Equal(1, img.Bands);
        Assert.Equal(ny * nz, img.Height);
        Assert.Equal("8", img.Metadata["nifti:dim3"]);
        Assert.Equal("8", img.Metadata["n-pages"]);

        // First slice's first row holds value 0; last slice's last row holds 7.
        Assert.Equal(0, ReadPel(img, 0, 0));
        Assert.Equal(7, ReadPel(img, 0, img.Height - 1));
    }

    [Fact]
    public async Task Nifti3DSmallZ_KeepsLegacyZAsBandsLayout()
    {
        // Back-compat: nz = 3 still uses the Z-as-bands path. No new
        // metadata, no height stacking.
        const int nx = 3, ny = 2, nz = 3, nt = 1;
        var pixels = new byte[nx * ny * nz];
        for (int z = 0; z < nz; z++)
            for (int i = 0; i < nx * ny; i++)
                pixels[z * nx * ny + i] = (byte)(z * 50);

        var nifti = BuildNifti(nx, ny, nz, nt, pixels);
        var img = await LoadAsync(nifti);

        Assert.Equal(3, img.Bands);
        Assert.Equal(ny, img.Height);
        // Legacy layout doesn't add the stacked metadata.
        Assert.False(img.Metadata.ContainsKey("nifti:dim3"));
        Assert.False(img.Metadata.ContainsKey("n-pages"));
    }

    [Fact]
    public async Task Nifti4D_TimeSeriesOrderingPreserved()
    {
        // Frames in the height stack must follow (T, Z) outer-to-inner
        // order — same convention as multi-page TIFF Z-then-T pages.
        // 1×1×3×2: 3 z-slices, 2 timepoints; values encode (t, z).
        const int nx = 1, ny = 1, nz = 3, nt = 2;
        var pixels = new byte[nx * ny * nz * nt];
        for (int t = 0; t < nt; t++)
            for (int z = 0; z < nz; z++)
                pixels[t * nz + z] = (byte)(t * 10 + z);

        var nifti = BuildNifti(nx, ny, nz, nt, pixels);
        var img = await LoadAsync(nifti);
        Assert.Equal(6, img.Height);

        // Frame index = t * nz + z; height row = frame index (since ny = 1).
        Assert.Equal(0, ReadPel(img, 0, 0));    // (t=0, z=0) → 0
        Assert.Equal(1, ReadPel(img, 0, 1));    // (t=0, z=1) → 1
        Assert.Equal(2, ReadPel(img, 0, 2));    // (t=0, z=2) → 2
        Assert.Equal(10, ReadPel(img, 0, 3));   // (t=1, z=0) → 10
        Assert.Equal(11, ReadPel(img, 0, 4));   // (t=1, z=1) → 11
        Assert.Equal(12, ReadPel(img, 0, 5));   // (t=1, z=2) → 12
    }
}
