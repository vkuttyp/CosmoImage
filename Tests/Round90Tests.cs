using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CosmoImage.Core;
using Xunit;

namespace CosmoImage.Tests;

public class Round90Tests : IDisposable
{
    /// <summary>
    /// Round-trippable custom format. Stream layout:
    ///   "MYFM"  (4 bytes magic)
    ///   width   (uint32 BE)
    ///   height  (uint32 BE)
    ///   bands   (uint8)
    ///   pixels  (W·H·bands bytes, row-major)
    /// </summary>
    private sealed class MyFormatProvider : IVipsImageFormat
    {
        public string Name => "MYFM";
        public bool CanEncode => true;

        public async ValueTask<bool> CanDecodeAsync(IVipsSource source, CancellationToken ct = default)
        {
            var sniff = await source.SniffAsync(4, ct);
            if (sniff.Length < 4) return false;
            var s = sniff.Span;
            return s[0] == 'M' && s[1] == 'Y' && s[2] == 'F' && s[3] == 'M';
        }

        public async ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken ct = default)
        {
            // Read header (13 bytes) + body.
            var header = new byte[13];
            int read = 0;
            while (read < 13)
            {
                int n = await source.ReadAsync(header.AsMemory(read, 13 - read), ct);
                if (n == 0) break;
                read += n;
            }
            if (read < 13) return null;
            int w = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
            int h = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
            int bands = header[12];
            var pixels = new byte[w * h * bands];
            read = 0;
            while (read < pixels.Length)
            {
                int n = await source.ReadAsync(pixels.AsMemory(read, pixels.Length - read), ct);
                if (n == 0) break;
                read += n;
            }
            var img = new VipsImage
            {
                Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
                Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            };
            img.GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                int rowBytes = w * bands;
                var pix = (byte[])a!;
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    int srcRow = (reg.Valid.Top + y) * rowBytes + reg.Valid.Left * bands;
                    pix.AsSpan(srcRow, reg.Valid.Width * bands)
                        .CopyTo(reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y));
                }
                return 0;
            };
            img.ClientA = pixels;
            return img;
        }

        public ValueTask SaveAsync(VipsImage image, Stream stream, CancellationToken ct = default)
        {
            stream.Write(new byte[] { (byte)'M', (byte)'Y', (byte)'F', (byte)'M' });
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)image.Width);
            stream.Write(u32);
            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)image.Height);
            stream.Write(u32);
            stream.WriteByte((byte)image.Bands);
            // Materialise + dump body. For tests we only support solid images
            // built via Generate so we just request all pixels.
            using var reg = new VipsRegion(image);
            reg.Prepare(new VipsRect(0, 0, image.Width, image.Height));
            for (int y = 0; y < image.Height; y++)
            {
                var row = reg.GetAddress(0, y).Slice(0, image.Width * image.Bands);
                stream.Write(row);
            }
            return default;
        }
    }

    /// <summary>Decoder-only provider — leaves CanEncode at false.</summary>
    private sealed class DecodeOnlyFormat : IVipsImageFormat
    {
        public string Name => "DECODE_ONLY";

        public async ValueTask<bool> CanDecodeAsync(IVipsSource source, CancellationToken ct = default)
        {
            var sniff = await source.SniffAsync(2, ct);
            return sniff.Length >= 2 && sniff.Span[0] == 'D' && sniff.Span[1] == 'O';
        }

        public ValueTask<VipsImage?> LoadAsync(IVipsSource source, CancellationToken ct = default)
            => ValueTask.FromResult<VipsImage?>(null);
    }

    public void Dispose() => VipsConfiguration.Default.Clear();

    private static VipsImage MakeSolidImage(int w, int h, int bands, byte fill)
    {
        var pixels = new byte[w * h * bands];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
        var img = new VipsImage
        {
            Width = w, Height = h, Bands = bands, BandFormat = VipsBandFormat.UChar,
            Interpretation = bands == 1 ? VipsInterpretation.BW : VipsInterpretation.RGB,
            ClientA = pixels,
        };
        img.GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
            int rowBytes = w * bands;
            var pix = (byte[])a!;
            for (int y = 0; y < reg.Valid.Height; y++)
            {
                int srcRow = (reg.Valid.Top + y) * rowBytes + reg.Valid.Left * bands;
                pix.AsSpan(srcRow, reg.Valid.Width * bands)
                    .CopyTo(reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y));
            }
            return 0;
        };
        return img;
    }

    // ---- CanEncode default ----

    [Fact]
    public void DefaultProvider_CanEncodeIsFalse()
    {
        IVipsImageFormat fmt = new DecodeOnlyFormat();
        Assert.False(fmt.CanEncode);
    }

    [Fact]
    public async Task DefaultSaveAsync_ThrowsNotSupported()
    {
        IVipsImageFormat fmt = new DecodeOnlyFormat();
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await fmt.SaveAsync(MakeSolidImage(2, 2, 3, 0), new MemoryStream()));
    }

    // ---- FindByName ----

    [Fact]
    public void FindByName_ReturnsRegisteredFormat()
    {
        VipsConfiguration.Default.Clear();
        var fmt = new MyFormatProvider();
        VipsConfiguration.Default.Register(fmt);
        Assert.Same(fmt, VipsConfiguration.Default.FindByName("MYFM"));
    }

    [Fact]
    public void FindByName_IsCaseInsensitive()
    {
        VipsConfiguration.Default.Clear();
        var fmt = new MyFormatProvider();
        VipsConfiguration.Default.Register(fmt);
        Assert.Same(fmt, VipsConfiguration.Default.FindByName("myfm"));
        Assert.Same(fmt, VipsConfiguration.Default.FindByName("MyFm"));
    }

    [Fact]
    public void FindByName_MissingReturnsNull()
    {
        VipsConfiguration.Default.Clear();
        Assert.Null(VipsConfiguration.Default.FindByName("nonexistent"));
    }

    [Fact]
    public void FindByName_NewerWins()
    {
        VipsConfiguration.Default.Clear();
        var older = new MyFormatProvider();
        var newer = new MyFormatProvider();
        VipsConfiguration.Default.Register(older);
        VipsConfiguration.Default.Register(newer);
        Assert.Same(newer, VipsConfiguration.Default.FindByName("MYFM"));
    }

    // ---- SaveAsync dispatch ----

    [Fact]
    public async Task SaveAsync_DispatchesToCustomFormat()
    {
        VipsConfiguration.Default.Clear();
        VipsConfiguration.Default.Register(new MyFormatProvider());
        var image = MakeSolidImage(3, 2, 3, 100);
        var stream = new MemoryStream();
        await VipsConfiguration.Default.SaveAsync(image, stream, "MYFM");
        var bytes = stream.ToArray();
        // Magic should be at start.
        Assert.Equal((byte)'M', bytes[0]);
        Assert.Equal((byte)'Y', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'M', bytes[3]);
        // 4 bytes magic + 4 width + 4 height + 1 bands + 18 pixels (3·2·3) = 31.
        Assert.Equal(31, bytes.Length);
    }

    [Fact]
    public async Task SaveAsync_UnknownNameThrows()
    {
        VipsConfiguration.Default.Clear();
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await VipsConfiguration.Default.SaveAsync(
                MakeSolidImage(2, 2, 3, 0), new MemoryStream(), "NoSuchFormat"));
    }

    [Fact]
    public async Task SaveAsync_DecodeOnlyFormatThrows()
    {
        VipsConfiguration.Default.Clear();
        VipsConfiguration.Default.Register(new DecodeOnlyFormat());
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await VipsConfiguration.Default.SaveAsync(
                MakeSolidImage(2, 2, 3, 0), new MemoryStream(), "DECODE_ONLY"));
    }

    // ---- Round-trip ----

    [Fact]
    public async Task RoundTrip_SaveThenLoad_RecoversImage()
    {
        VipsConfiguration.Default.Clear();
        VipsConfiguration.Default.Register(new MyFormatProvider());

        var original = MakeSolidImage(4, 3, 3, 200);
        var stream = new MemoryStream();
        await VipsConfiguration.Default.SaveAsync(original, stream, "MYFM");

        // Now load via the read side (Configuration.Default's
        // custom-format dispatch into VipsIdentify.LoadAsync).
        stream.Position = 0;
        await using var source = new PipeVipsSource(PipeReader.Create(stream));
        var loaded = await Loaders.VipsIdentify.LoadAsync(source);
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Width);
        Assert.Equal(3, loaded.Height);
        Assert.Equal(3, loaded.Bands);
        // Verify pixel values.
        using var reg = new VipsRegion(loaded);
        reg.Prepare(new VipsRect(0, 0, 4, 3));
        var addr = reg.GetAddress(0, 0);
        for (int i = 0; i < 12; i++) Assert.Equal(200, addr[i]);
    }

    // ---- Validation ----

    [Fact]
    public async Task SaveAsync_NullImageThrows()
    {
        VipsConfiguration.Default.Clear();
        VipsConfiguration.Default.Register(new MyFormatProvider());
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await VipsConfiguration.Default.SaveAsync(null!, new MemoryStream(), "MYFM"));
    }

    [Fact]
    public async Task SaveAsync_NullStreamThrows()
    {
        VipsConfiguration.Default.Clear();
        VipsConfiguration.Default.Register(new MyFormatProvider());
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await VipsConfiguration.Default.SaveAsync(
                MakeSolidImage(2, 2, 3, 0), null!, "MYFM"));
    }

    [Fact]
    public async Task SaveAsync_NullFormatNameThrows()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await VipsConfiguration.Default.SaveAsync(
                MakeSolidImage(2, 2, 3, 0), new MemoryStream(), null!));
    }
}
