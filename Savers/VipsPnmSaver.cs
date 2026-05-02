using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace CosmoImage.Savers;

public enum VipsPnmVariant
{
    /// <summary>Auto: pick PBM/PGM/PPM by band count.</summary>
    Auto = 0,
    Pbm = 1, // bitmap (1 band, binarized)
    Pgm = 2, // grayscale
    Ppm = 3, // RGB
    Pam = 4, // arbitrary-band (preserves alpha)
}

public static class VipsPnmSaver
{
    public static Task SaveAsync(VipsImage image, PipeWriter writer, VipsPnmVariant variant = VipsPnmVariant.Auto, CancellationToken cancellationToken = default)
    {
        var fmt = variant switch
        {
            VipsPnmVariant.Pbm => MagickFormat.Pbm,
            VipsPnmVariant.Pgm => MagickFormat.Pgm,
            VipsPnmVariant.Ppm => MagickFormat.Ppm,
            VipsPnmVariant.Pam => MagickFormat.Pam,
            _ => image.Bands switch
            {
                1 => MagickFormat.Pgm,
                2 or 4 => MagickFormat.Pam, // PAM preserves alpha
                _ => MagickFormat.Ppm,
            }
        };
        return VipsMagickWrapSaver.SaveAsync(image, writer, fmt, cancellationToken);
    }
}
