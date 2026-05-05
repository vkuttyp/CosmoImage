using System;
using System.Runtime.CompilerServices;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// 45-degree-increment rotation around the image centre. Mirrors libvips'
/// <c>rot45</c>: input must be square with an odd side length so the
/// rotation centre lands on a pixel. Out-of-bounds samples after rotation
/// are zero-filled — this matches the SE-generation use case (non-axis
/// structuring elements for <c>morph</c>) where the corners turn into
/// diamond points.
///
/// <para>Axis-aligned angles (D0/D90/D180/D270) produce pixel-identical
/// output to <see cref="VipsRotate"/>. The diagonal angles
/// (D45/D135/D225/D315) sample by the rotation matrix with nearest-
/// neighbour resampling.</para>
///
/// <para>Works with any band count and pixel format — the input pel is
/// copied verbatim, so UChar, UShort, Float, etc. all pass through.</para>
/// </summary>
public class VipsRot45 : VipsOperation
{
    public VipsImage? In { get; set; }
    public VipsImage? Out { get; set; }
    public VipsAngle45 Angle { get; set; } = VipsAngle45.D45;

    public override int Build()
    {
        if (In == null) return -1;
        if (In.Width != In.Height) return -1;          // libvips: square only
        if ((In.Width & 1) == 0) return -1;            // libvips: odd side only

        Out = new VipsImage
        {
            Width = In.Width,
            Height = In.Height,
            Bands = In.Bands,
            BandFormat = In.BandFormat,
            Interpretation = In.Interpretation,
            Coding = In.Coding,
            XRes = In.XRes,
            YRes = In.YRes,
            StartFn = VipsSeq.StartOne,
            GenerateFn = Generate,
            StopFn = VipsSeq.StopOne,
            ClientA = In,
            ClientB = Angle,
        };
        Out.CopyMetadataFrom(In);
        // 45° angles need random access across the input — request the
        // whole image as one prep. Axis-aligned angles still have
        // fan-out access but go through their own switch arms.
        Out.SetPipeline(VipsDemandStyle.SmallTile, In);
        return 0;
    }

    public override int GetCacheKey()
        => HashCode.Combine("Rot45", RuntimeHelpers.GetHashCode(In), Angle);

    private static int Generate(VipsRegion outRegion, object? seq, object? a, object? b, ref bool stop)
    {
        var inRegion = (VipsRegion)seq!;
        VipsImage @in = inRegion.Image;
        var angle = (VipsAngle45)b!;
        VipsRect r = outRegion.Valid;
        int pelSize = @in.SizeOfPel;
        int n = @in.Width;
        int half = (n - 1) / 2;

        // For all eight angles, request the entire input — the rotated
        // mapping is non-local and may pull from anywhere.
        if (inRegion.Prepare(new VipsRect(0, 0, n, n)) != 0) return -1;

        // Inverse-rotation table: cos(-θ), -sin(-θ), sin(-θ), cos(-θ).
        // We sample input from rotated coordinates: (ix, iy) = R(-θ) · (ox, oy)
        // around the centre (half, half).
        (double c, double s) = angle switch
        {
            VipsAngle45.D0   => ( 1.0,  0.0),
            VipsAngle45.D45  => ( Cos45,  Sin45),
            VipsAngle45.D90  => ( 0.0,  1.0),
            VipsAngle45.D135 => (-Sin45,  Cos45),
            VipsAngle45.D180 => (-1.0,  0.0),
            VipsAngle45.D225 => (-Cos45, -Sin45),
            VipsAngle45.D270 => ( 0.0, -1.0),
            VipsAngle45.D315 => ( Sin45, -Cos45),
            _ => (1.0, 0.0),
        };

        for (int y = 0; y < r.Height; y++)
        {
            var outAddr = outRegion.GetAddress(r.Left, r.Top + y);
            int oy = r.Top + y;
            int dy = oy - half;
            for (int x = 0; x < r.Width; x++)
            {
                int ox = r.Left + x;
                int dx = ox - half;
                // Nearest-neighbour inverse rotation. cos/sin already
                // encode the inverse direction (R(-θ)).
                int ix = half + (int)Math.Round(c * dx + s * dy);
                int iy = half + (int)Math.Round(-s * dx + c * dy);
                var dst = outAddr.Slice(x * pelSize, pelSize);
                if ((uint)ix < (uint)n && (uint)iy < (uint)n)
                    inRegion.GetAddress(ix, iy).Slice(0, pelSize).CopyTo(dst);
                else
                    dst.Clear();
            }
        }
        return 0;
    }

    private const double Cos45 = 0.7071067811865476; // √2/2
    private const double Sin45 = 0.7071067811865476;
}
