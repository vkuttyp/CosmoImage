using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Savers;

public static class VipsQoiSaver
{
    public static Task SaveAsync(VipsImage image, PipeWriter writer, CancellationToken cancellationToken = default)
        => VipsMagickWrapSaver.SaveAsync(image, writer, MagickFormat.Qoi, cancellationToken);
}
