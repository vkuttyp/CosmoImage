using System.IO;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using CosmoImage.Loaders;
using CosmoImage.Savers;
using Xunit;

namespace CosmoImage.Tests;

public class PngXmpTests
{
    [Fact]
    public async Task PngSaveLoad_XmpBlobInItxt_RoundTrips()
    {
        const string xmp = "<?xpacket begin='﻿' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'/></x:xmpmeta><?xpacket end='w'?>";
        var xmpBytes = Encoding.UTF8.GetBytes(xmp);

        var src = new VipsImage
        {
            Width = 4, Height = 4, Bands = 3, BandFormat = VipsBandFormat.UChar,
            Interpretation = VipsInterpretation.RGB,
            GenerateFn = (VipsRegion reg, object? seq, object? a, object? b, ref bool stop) => {
                for (int y = 0; y < reg.Valid.Height; y++)
                {
                    var addr = reg.GetAddress(reg.Valid.Left, reg.Valid.Top + y);
                    for (int i = 0; i < reg.Valid.Width * 3; i++) addr[i] = 128;
                }
                return 0;
            }
        };
        src.MetadataBlobs["xmp"] = xmpBytes;

        // Saver calls writer.CompleteAsync(), which closes the underlying
        // MemoryStream. Snapshot bytes via PipeWriter that targets a fresh
        // stream we own, then re-open as a new stream for the loader.
        byte[] pngBytes;
        using (var ms = new MemoryStream())
        {
            var writer = PipeWriter.Create(ms, new StreamPipeWriterOptions(leaveOpen: true));
            await VipsPngSaver.SaveAsync(src, writer);
            pngBytes = ms.ToArray();
        }

        var source = new PipeVipsSource(PipeReader.Create(new MemoryStream(pngBytes)));
        var loaded = await VipsPngLoader.LoadHeaderAsync(source);
        Assert.NotNull(loaded);
        Assert.True(loaded!.MetadataBlobs.ContainsKey("xmp"));
        Assert.Equal(xmpBytes, loaded.MetadataBlobs["xmp"]);
    }
}
