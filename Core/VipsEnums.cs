namespace CosmoImage.Core;

public enum VipsBandFormat
{
    NotSet = -1,
    UChar = 0,
    Char = 1,
    UShort = 2,
    Short = 3,
    UInt = 4,
    Int = 5,
    Float = 6,
    Complex = 7,
    Double = 8,
    DPComplex = 9,
    /// <summary>16-bit IEEE 754 float (System.Half). 2 bytes per band.</summary>
    Half = 10,
    Last = 11
}

public enum VipsInterpretation
{
    Error = -1,
    Multiband = 0,
    BW = 1,
    Histogram = 10,
    XYZ = 12,
    Lab = 13,
    CMYK = 15,
    LabQ = 16,
    RGB = 17,
    CMC = 18,
    LCh = 19,
    LabS = 21,
    SRGB = 22,
    YXY = 23,
    Fourier = 24,
    RGB16 = 25,
    Grey16 = 26,
    Matrix = 27,
    scRGB = 28,
    HSV = 29,
    OkLab = 30,
    OkLCh = 31,
    Last = 32
}

public enum VipsCoding
{
    Error = -1,
    None = 0,
    LabQ = 2,
    Rad = 6,
    Last = 7
}

public enum VipsAccess
{
    Random = 0,
    Sequential = 1,
    SequentialUnbuffered = 2,
    Last = 3
}

public enum VipsImageType
{
    Error = -1,
    None = 0,
    SetBuf = 1,
    SetBufForeign = 2,
    OpenIn = 3,
    MMapIn = 4,
    MMapInRW = 5,
    OpenOut = 6,
    Partial = 7
}

public enum VipsDemandStyle
{
    Error = -1,
    SmallTile = 0,
    FatStrip = 1,
    ThinStrip = 2,
    Any = 3
}

public enum VipsPrecision
{
    Integer = 0,
    Float = 1,
    Approximate = 2,
    Last = 3
}

public enum VipsDirection
{
    Horizontal = 0,
    Vertical = 1,
    Last = 2
}

/// <summary>
/// Cross-axis alignment for compose ops (Join etc). For a horizontal
/// join, <c>Low</c> is top-aligned and <c>High</c> is bottom-aligned;
/// vertical join, <c>Low</c> is left-aligned and <c>High</c> is right.
/// </summary>
public enum VipsAlign
{
    Low = 0,
    Centre = 1,
    High = 2,
    Last = 3
}

public enum VipsAngle
{
    D0 = 0,
    D90 = 1,
    D180 = 2,
    D270 = 3,
    Last = 4
}

/// <summary>
/// 45-degree rotation set used by <see cref="CosmoImage.Operations.Geometric.VipsRot45"/>.
/// libvips' <c>rot45</c> rotates square (odd-sided) images by these increments;
/// chiefly used to produce non-axis-aligned structuring elements for
/// mathematical morphology.
/// </summary>
public enum VipsAngle45
{
    D0 = 0,
    D45 = 1,
    D90 = 2,
    D135 = 3,
    D180 = 4,
    D225 = 5,
    D270 = 6,
    D315 = 7,
}

/// <summary>
/// Anchor positions for ops that place a smaller image inside a larger
/// canvas (<see cref="CosmoImage.Operations.Geometric.VipsEmbed"/> via
/// <c>Pad</c>). Mirrors libvips <c>VipsCompassDirection</c>.
/// </summary>
public enum VipsCompass
{
    Centre = 0,
    North = 1,
    East = 2,
    South = 3,
    West = 4,
    NorthEast = 5,
    SouthEast = 6,
    SouthWest = 7,
    NorthWest = 8,
}

public enum VipsKernel
{
    Nearest = 0,
    Linear = 1,
    Cubic = 2,           // Catmull-Rom (B=0, C=0.5)
    Mitchell = 3,        // Mitchell-Netravali (B=1/3, C=1/3)
    Lanczos2 = 4,
    Lanczos3 = 5,
    Lanczos5 = 6,        // Wider Lanczos — better antialiasing on steep downscales
    Hermite = 7,         // Cubic Hermite spline, support 1
    BicubicSharper = 8,  // BC family with B=0, C=1 — sharpens
    BicubicSmoother = 9, // BC family with B=1.5, C=-0.25 — softer than Mitchell
    Last = 10
}


