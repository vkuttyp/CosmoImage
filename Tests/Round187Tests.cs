using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

/// <summary>
/// Round 187 — pure-C# GIF saver. Replaces the Magick-backed
/// <see cref="VipsGifSaver"/> with a hand-rolled GIF89a emitter
/// (LZW encoder + chunk framing). Drops the last GIF-side Magick
/// dependency.
///
/// <para>Tests verify the wire format (GIF89a magic, NETSCAPE
/// loop extension for animated, trailer byte) and round-trip via
/// <see cref="VipsGifLoader"/> which uses the existing pure-C#
/// <c>PureGifDecoder</c>.</para>
/// </summary>
public class Round187Tests
{
    private static VipsImage MakeRgb(int w, int h, Func<int, int, (byte R, byte G, byte B)> pixel)
        => new VipsImage
        {
            Width = w, Height = h, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        var (r, g, bl) = pixel(reg.Valid.Left + x, reg.Valid.Top + y);
                        addr[x * 3 + 0] = r;
                        addr[x * 3 + 1] = g;
                        addr[x * 3 + 2] = bl;
                    }
                }
                return 0;
            }
        };

    /// <summary>3-frame stacked-RGB animation: red / green / blue solid frames.</summary>
    private static VipsImage Make3FrameAnim(int frameW, int frameH)
    {
        int totalH = frameH * 3;
        var img = new VipsImage
        {
            Width = frameW, Height = totalH, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) =>
            {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    int gy = reg.Valid.Top + y;
                    int frame = gy / frameH;
                    var addr = reg.GetAddress(reg.Valid.Left, gy);
                    for (int x = 0; x < reg.Valid.Width; x++)
                    {
                        addr[x * 3 + 0] = (byte)(frame == 0 ? 255 : 0);
                        addr[x * 3 + 1] = (byte)(frame == 1 ? 255 : 0);
                        addr[x * 3 + 2] = (byte)(frame == 2 ? 255 : 0);
                    }
                }
                return 0;
            }
        };
        img.Metadata["n-pages"] = "3";
        img.Metadata["page-height"] = frameH.ToString();
        img.Metadata["animation-delays"] = "5,10,15";
        return img;
    }

    private static async Task<byte[]> SaveAsync(VipsImage img)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await VipsGifSaver.SaveAsync(img, writer);
        return ms.ToArray();
    }

    [Fact]
    public async Task Header_StartsWithGif89a()
    {
        var img = MakeRgb(8, 8, (x, y) => ((byte)(x * 32), (byte)(y * 32), 100));
        var bytes = await SaveAsync(img);
        Assert.True(bytes.Length >= 6);
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
    }

    [Fact]
    public async Task Trailer_IsLastByte()
    {
        var img = MakeRgb(4, 4, (x, y) => ((byte)50, (byte)100, (byte)150));
        var bytes = await SaveAsync(img);
        Assert.Equal(0x3B, bytes[^1]);
    }

    [Fact]
    public async Task Animated_EmitsNetscapeLoopExtension()
    {
        var img = Make3FrameAnim(4, 4);
        var bytes = await SaveAsync(img);
        // NETSCAPE2.0 signature should appear somewhere in the byte stream.
        var marker = System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0");
        bool found = false;
        for (int i = 0; i < bytes.Length - marker.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < marker.Length; j++)
                if (bytes[i + j] != marker[j]) { match = false; break; }
            if (match) { found = true; break; }
        }
        Assert.True(found);
    }

    [Fact]
    public async Task SingleFrame_NoNetscapeExtension()
    {
        var img = MakeRgb(4, 4, (x, y) => ((byte)100, (byte)100, (byte)100));
        var bytes = await SaveAsync(img);
        var marker = System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0");
        bool found = false;
        for (int i = 0; i < bytes.Length - marker.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < marker.Length; j++)
                if (bytes[i + j] != marker[j]) { match = false; break; }
            if (match) { found = true; break; }
        }
        Assert.False(found);
    }

    [Fact]
    public async Task RoundTrip_SingleFrame_PreservesContent()
    {
        // Use a small, palette-friendly image so quantization is exact.
        var src = MakeRgb(4, 4, (x, y) =>
        {
            int idx = (y * 4 + x) % 4;
            return idx switch
            {
                0 => ((byte)0, (byte)0, (byte)0),
                1 => ((byte)255, (byte)0, (byte)0),
                2 => ((byte)0, (byte)255, (byte)0),
                _ => ((byte)0, (byte)0, (byte)255),
            };
        });
        var bytes = await SaveAsync(src);

        var srcReader = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsGifLoader.LoadAsync(srcReader);
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Width);
        Assert.Equal(4, loaded.Height);

        // PureGifDecoder returns RGBA — alpha is opaque (255) for non-transparent frames.
        Assert.Equal(4, loaded.Bands);
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 4));
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                var addr = reg.GetAddress(0, y);
                int idx = (y * 4 + x) % 4;
                var (er, eg, eb) = idx switch
                {
                    0 => ((byte)0, (byte)0, (byte)0),
                    1 => ((byte)255, (byte)0, (byte)0),
                    2 => ((byte)0, (byte)255, (byte)0),
                    _ => ((byte)0, (byte)0, (byte)255),
                };
                Assert.Equal(er, addr[x * 4 + 0]);
                Assert.Equal(eg, addr[x * 4 + 1]);
                Assert.Equal(eb, addr[x * 4 + 2]);
            }
    }

    [Fact]
    public async Task RoundTrip_Animated_PreservesFrameCount()
    {
        var src = Make3FrameAnim(4, 4);
        var bytes = await SaveAsync(src);

        var srcReader = new PipeVipsSource(PipeReader.Create(new MemoryStream(bytes)));
        var loaded = await VipsGifLoader.LoadAsync(srcReader);
        Assert.NotNull(loaded);

        Assert.Equal("3", loaded!.Metadata["n-pages"]);
        Assert.Equal("4", loaded.Metadata["page-height"]);
        Assert.Equal(12, loaded.Height);
    }
}
