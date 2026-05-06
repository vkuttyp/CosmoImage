namespace CosmoImage.Loaders;

internal static class PureJpegDecoder
{
    public static bool TryReadHeader(byte[] jpeg, out int width, out int height, out int channels) =>
        CosmoImagePdf.Shared.Imaging.PureJpegDecoder.TryReadHeader(jpeg, out width, out height, out channels);

    public static byte[]? TryDecode(byte[] jpeg, out int width, out int height, out int channels) =>
        CosmoImagePdf.Shared.Imaging.PureJpegDecoder.TryDecode(jpeg, out width, out height, out channels);
}
