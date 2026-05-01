using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Xunit;

namespace CosmoImage.Tests;

public class SaverTests
{
    [Fact]
    public async Task SaveAsync_SimpleImage_SavesValidJpeg()
    {
        // Arrange: 64x64 grayscale image
        var image = new VipsImage
        {
            Width = 64, Height = 64, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 128;
                }
                return 0;
            }
        };

        var pipe = new Pipe();
        
        // Act: Save to pipe
        var saveTask = VipsJpegSaver.SaveAsync(image, pipe.Writer);
        
        // Load from pipe to verify
        await using var source = new PipeVipsSource(pipe.Reader);
        var loaded = await VipsJpegLoader.LoadAsync(source);
        
        await saveTask;

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(64, loaded.Width);
        Assert.Equal(64, loaded.Height);
        Assert.Equal(1, loaded.Bands);
    }

    [Fact]
    public async Task SaveAsync_SimpleImage_SavesValidPng()
    {
        // Arrange: 32x32 RGB image
        var image = new VipsImage
        {
            Width = 32, Height = 32, Bands = 3, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width * 3; x++) addr[x] = 200;
                }
                return 0;
            }
        };

        var pipe = new Pipe();
        
        // Act: Save to pipe
        var saveTask = VipsPngSaver.SaveAsync(image, pipe.Writer);
        
        // Load from pipe to verify (using our PNG header loader)
        await using var source = new PipeVipsSource(pipe.Reader);
        var loaded = await VipsPngLoader.LoadHeaderAsync(source);
        
        await saveTask;

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(32, loaded.Width);
        Assert.Equal(32, loaded.Height);
        Assert.Equal(3, loaded.Bands);
    }

    [Fact]
    public async Task SaveAsync_SimpleImage_SavesValidWebp()
    {
        // Arrange: 16x16 RGB image
        var image = new VipsImage
        {
            Width = 16, Height = 16, Bands = 3, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width * 3; x++) addr[x] = 150;
                }
                return 0;
            }
        };

        var pipe = new Pipe();
        
        // Act: Save to pipe
        var saveTask = VipsWebpSaver.SaveAsync(image, pipe.Writer);
        
        // Load from pipe to verify (using our WebP header loader)
        await using var source = new PipeVipsSource(pipe.Reader);
        var loaded = await VipsWebpLoader.LoadHeaderAsync(source);
        
        await saveTask;

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(16, loaded.Width);
        Assert.Equal(16, loaded.Height);
        Assert.Equal(3, loaded.Bands);
    }

    [Fact]
    public async Task SaveAsync_SimpleImage_SavesValidTiff()
    {
        // Arrange: 10x20 grayscale image
        var image = new VipsImage
        {
            Width = 10, Height = 20, Bands = 1, BandFormat = VipsBandFormat.UChar,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++) {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int x = 0; x < reg.Valid.Width; x++) addr[x] = 200;
                }
                return 0;
            }
        };

        var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
        
        // Act: Save to pipe (which writes to MemoryStream)
        await VipsTiffSaver.SaveAsync(image, writer);
        
        // Load from MemoryStream to verify
        ms.Position = 0;
        await using var source = new PipeVipsSource(PipeReader.Create(ms));
        var loaded = await VipsTiffLoader.LoadHeaderAsync(source);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(10, loaded.Width);
        Assert.Equal(20, loaded.Height);
        Assert.Equal(1, loaded.Bands);
    }
}

