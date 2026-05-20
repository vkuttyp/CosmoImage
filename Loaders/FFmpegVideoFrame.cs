using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// First-frame thumbnail extraction for video files via shell-out to <c>ffmpeg</c>. The
/// shipped image loaders can decode stills but not container formats — to give chat / message
/// UIs an inline preview for an outgoing video, we need to render the first decodable frame as
/// a JPEG. ffmpeg does this in a single subprocess invocation; the output streams back over
/// stdout so we never need a temp file for the encoded frame.
///
/// <para>Same soft-dependency contract as <see cref="FFprobeMediaIdentify"/>: missing ffmpeg
/// binary, undecodable input, or "no video stream" all produce a null result. The caller is
/// expected to fall back to a generic icon.</para>
/// </summary>
public static class FFmpegVideoFrame
{
    /// <summary>
    /// Extract the first decodable video frame from a file path, scaled to fit within
    /// <paramref name="maxDim"/> on the longer edge, encoded as JPEG. Aspect ratio preserved.
    /// </summary>
    public static Task<byte[]?> ExtractFirstFrameAsync(string filePath, int maxDim = 320, int quality = 75, CancellationToken ct = default)
        => RunAsync(filePath, maxDim, quality, ct);

    /// <summary>
    /// Extract the first decodable video frame from in-memory bytes. Writes a temp file so
    /// ffmpeg has a seekable input — feeding via stdin works only for raw streams and breaks
    /// on container formats whose moov atom lives at the end of the file (the common iOS /
    /// Android camera shape).
    /// </summary>
    /// <param name="extensionHint">Extension like <c>.mp4</c> / <c>.webm</c> to help ffmpeg's
    /// demuxer auto-detect; <c>null</c> lets ffmpeg sniff. Recommended for mp4/mov.</param>
    public static async Task<byte[]?> ExtractFirstFrameAsync(
        byte[] bytes,
        string? extensionHint = null,
        int maxDim = 320,
        int quality = 75,
        CancellationToken ct = default)
    {
        if (bytes is null || bytes.Length == 0) return null;
        var ext = NormalizeExtension(extensionHint);
        var temp = Path.Combine(Path.GetTempPath(), $"cosmoimage-ffframe-{Guid.NewGuid():N}{ext}");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            return await RunAsync(temp, maxDim, quality, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* opportunistic cleanup */ }
        }
    }

    private static async Task<byte[]?> RunAsync(string filePath, int maxDim, int quality, CancellationToken ct)
    {
        // - vframes 1                  : exit after one decoded frame
        // - vf scale=…                  : long-edge cap with aspect-preserving scaling.
        //                                 force_original_aspect_ratio=decrease prevents upscale.
        // - q:v <2..31>                 : ffmpeg's mjpeg quality scale, INVERTED relative to
        //                                 typical libjpeg (lower = better). 2 maps to ~95, 31
        //                                 to ~10; the public `quality` param uses libjpeg
        //                                 semantics so we convert.
        // - f image2pipe / mjpeg        : write MJPEG bytes to stdout (a single JPEG frame).
        var ffmpegQ = MapQuality(quality);
        var args = $"-v error -i \"{filePath}\" -vframes 1 " +
                   $"-vf \"scale='min({maxDim},iw)':'min({maxDim},ih)':force_original_aspect_ratio=decrease\" " +
                   $"-q:v {ffmpegQ} -f image2pipe -c:v mjpeg pipe:1";

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? proc = null;
        try { proc = Process.Start(psi); }
        catch
        {
            // ffmpeg not on PATH — treat as "no thumbnail available".
            return null;
        }
        if (proc is null) return null;

        try
        {
            using var bufferedOut = new MemoryStream();
            // Stdout is a binary JPEG — read it as a byte stream, not a string.
            var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(bufferedOut, ct);
            // Drain stderr concurrently to avoid pipe-fill deadlocks on ffmpeg's banner output.
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(copyTask, stderrTask).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0 || bufferedOut.Length == 0) return null;
            return bufferedOut.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Convert a libjpeg-style quality (0–100, higher = better) to ffmpeg's mjpeg <c>q:v</c>
    /// scale (2–31, lower = better). Approximate; pixel-perfect parity is unnecessary for the
    /// preview-thumbnail use case.
    /// </summary>
    private static int MapQuality(int libjpegQuality)
    {
        if (libjpegQuality <= 0) return 31;
        if (libjpegQuality >= 100) return 2;
        // Linear-ish map: 100 → 2, 50 → ~16, 1 → ~31.
        var q = 31 - (int)Math.Round((libjpegQuality / 100.0) * 29.0);
        return Math.Clamp(q, 2, 31);
    }

    private static string NormalizeExtension(string? hint)
    {
        if (string.IsNullOrEmpty(hint)) return string.Empty;
        return hint.StartsWith('.') ? hint : "." + hint;
    }
}
