using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CosmoImage.Loaders;

/// <summary>
/// Metadata for a container the image-loader stack can't open on its own — primarily
/// audio + video. Mirrors the shape of <see cref="VipsIdentifyResult"/> but speaks
/// "duration in seconds" instead of pixel-only fields, and adds an optional codec name.
/// </summary>
/// <param name="Duration">Total media duration, when reported by the container.</param>
/// <param name="Width">Video frame width in pixels, or null for audio-only / unknown.</param>
/// <param name="Height">Video frame height in pixels, or null for audio-only / unknown.</param>
/// <param name="Codec">Primary stream codec name (e.g. "h264", "aac", "vorbis"), or null when
/// the container doesn't expose it cleanly.</param>
/// <param name="Bitrate">Container-level bit-rate in bits/second, when reported.</param>
public sealed record FFprobeMediaIdentifyResult(
    TimeSpan? Duration,
    int? Width,
    int? Height,
    string? Codec,
    long? Bitrate);

/// <summary>
/// Audio + video metadata identification via shell-out to <c>ffprobe</c>. The shipped image
/// loaders cover stills (PNG / JPEG / WebP / HEIF / …) and PDF; containers like mp4 / ogg /
/// matroska / wav are out of libvips' scope. This helper fills the gap by deferring to ffprobe
/// — universally available, returns structured JSON, and produces the same shape of metadata
/// (duration / dimensions / codec) the chat / messaging surfaces actually need.
///
/// <para>The helper is a soft dependency: if ffprobe isn't on PATH, every call returns null.
/// Callers are expected to treat the result as best-effort metadata, never fatal.</para>
///
/// <para>The shell-out runs with <c>-v error</c> so ffprobe's own startup chatter doesn't
/// reach our stderr, and with explicit <c>-show_format -show_streams -of json</c> so the
/// output schema is stable across ffmpeg versions.</para>
/// </summary>
public static class FFprobeMediaIdentify
{
    /// <summary>
    /// Probe a file path. Use when the media already lives on disc — saves a copy.
    /// </summary>
    public static Task<FFprobeMediaIdentifyResult?> IdentifyAsync(string filePath, CancellationToken ct = default)
        => RunAsync(filePath, ct);

    /// <summary>
    /// Probe an in-memory buffer. Writes the bytes to a temp file because ffprobe needs a
    /// seekable input for most container formats — feeding it via stdin works for raw streams
    /// only and is unreliable for mp4/m4a/etc which keep their metadata at the end of the file.
    /// </summary>
    /// <param name="bytes">The full media payload.</param>
    /// <param name="extensionHint">An extension like <c>.mp4</c> or <c>.ogg</c> that helps
    /// ffprobe pick the right demuxer when magic-byte detection is ambiguous (Ogg-vs-Opus).
    /// Pass <c>null</c> to let ffprobe auto-detect.</param>
    public static async Task<FFprobeMediaIdentifyResult?> IdentifyAsync(
        byte[] bytes,
        string? extensionHint = null,
        CancellationToken ct = default)
    {
        if (bytes is null || bytes.Length == 0) return null;
        var ext = NormalizeExtension(extensionHint);
        var temp = Path.Combine(Path.GetTempPath(), $"cosmoimage-ffprobe-{Guid.NewGuid():N}{ext}");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            return await RunAsync(temp, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* opportunistic cleanup */ }
        }
    }

    private static async Task<FFprobeMediaIdentifyResult?> RunAsync(string filePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffprobe",
            // -v error: suppress informational stderr lines so a successful probe is silent.
            // -show_format / -show_streams: emit container + per-stream metadata.
            // -of json: structured output we can parse safely (vs the default key=value flat form).
            Arguments = $"-v error -show_format -show_streams -of json -- \"{filePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? proc = null;
        try { proc = Process.Start(psi); }
        catch
        {
            // ffprobe not on PATH (or otherwise unlaunchable) — treat as "no metadata available".
            return null;
        }
        if (proc is null) return null;

        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            // Drain stderr concurrently to avoid backpressure on a verbose error path; we don't
            // surface it but reading it prevents the pipe from filling and deadlocking ffprobe.
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0) return null;

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return Parse(stdout);
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

    private static FFprobeMediaIdentifyResult? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            TimeSpan? duration = null;
            long? bitrate = null;
            if (root.TryGetProperty("format", out var fmt))
            {
                if (fmt.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.String
                    && double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var secs))
                    duration = TimeSpan.FromSeconds(secs);
                if (fmt.TryGetProperty("bit_rate", out var br) && br.ValueKind == JsonValueKind.String
                    && long.TryParse(br.GetString(), out var brVal))
                    bitrate = brVal;
            }

            int? width = null;
            int? height = null;
            string? codec = null;

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                // Prefer the first video stream for dims + codec; fall back to the first stream
                // (typically audio for audio-only containers) so we still surface codec name.
                JsonElement? videoStream = null;
                JsonElement? fallbackStream = null;
                foreach (var s in streams.EnumerateArray())
                {
                    fallbackStream ??= s;
                    if (s.TryGetProperty("codec_type", out var ct2) && ct2.ValueKind == JsonValueKind.String
                        && string.Equals(ct2.GetString(), "video", StringComparison.Ordinal))
                    {
                        videoStream = s;
                        break;
                    }
                }
                var chosen = videoStream ?? fallbackStream;
                if (chosen is { } stream)
                {
                    if (stream.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number) width = w.GetInt32();
                    if (stream.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) height = h.GetInt32();
                    if (stream.TryGetProperty("codec_name", out var c) && c.ValueKind == JsonValueKind.String) codec = c.GetString();
                    // Some containers (mp3, wav) report duration only on the stream, not on format.
                    if (duration is null && stream.TryGetProperty("duration", out var sd) && sd.ValueKind == JsonValueKind.String
                        && double.TryParse(sd.GetString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var ssecs))
                        duration = TimeSpan.FromSeconds(ssecs);
                }
            }

            if (duration is null && width is null && height is null && codec is null && bitrate is null) return null;
            return new FFprobeMediaIdentifyResult(duration, width, height, codec, bitrate);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeExtension(string? hint)
    {
        if (string.IsNullOrEmpty(hint)) return string.Empty;
        return hint.StartsWith('.') ? hint : "." + hint;
    }
}
