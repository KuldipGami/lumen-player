using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LumenPlayer.Models;

public record ResolvedStream(string VideoUrl, string? AudioUrl, string Title, string? Uploader, long DurationMs, bool IsLive);

/// <summary>
/// Turns web page links (YouTube, Vimeo, Twitch and the ~1800 other sites yt-dlp knows) into direct
/// stream URLs LibVLC can play. Uses the yt-dlp.exe bundled in the Tools folder.
/// </summary>
public static class StreamResolver
{
    public static string ToolPath => Path.Combine(AppContext.BaseDirectory, "Tools", "yt-dlp.exe");
    public static bool IsAvailable => File.Exists(ToolPath);

    // Prefer H.264 (hardware-decoded almost everywhere) up to 1080p, with the best AAC/Opus audio.
    private const string FormatSort = "res:1080,vcodec:h264,acodec:m4a";

    // Trust the Windows certificate store instead of yt-dlp's bundled list, so HTTPS works behind
    // antivirus web shields and corporate proxies that re-sign traffic with their own root certificate.
    private static readonly string[] NoCertifi = { "--compat-options", "no-certifi" };

    /// <summary>
    /// Direct media URLs and streaming protocols go straight to LibVLC. Ordinary web pages need resolving.
    /// </summary>
    public static bool NeedsResolving(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (MediaFormats.IsMedia("x" + ext) || MediaFormats.Playlists.Contains(ext) || ext.Equals(".mpd", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public static bool IsKnownVideoSite(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        new[] { "youtube.com", "youtu.be", "vimeo.com", "twitch.tv", "dailymotion.com", "tiktok.com", "facebook.com", "instagram.com", "x.com", "twitter.com" }
            .Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    public static async Task<ResolvedStream> ResolveAsync(string url, CancellationToken token)
    {
        if (!IsAvailable) throw new InvalidOperationException("YouTube support (yt-dlp) is missing from the Tools folder.");

        var psi = new ProcessStartInfo(ToolPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { NoCertifi[0], NoCertifi[1], "--dump-single-json", "--no-playlist", "--no-warnings", "--no-progress", "-f", "bv*+ba/b", "-S", FormatSort, url })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start yt-dlp.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(CleanError(await stderr));
            return Parse(await stdout);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
    }

    private static ResolvedStream Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        string? video = null, audio = null;
        if (root.TryGetProperty("requested_formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                var hasVideo = Str(f, "vcodec") is { } vc && vc != "none";
                if (hasVideo) video ??= Str(f, "url");
                else audio ??= Str(f, "url");
            }
        }
        video ??= Str(root, "url") ?? audio;
        if (video == audio) audio = null;
        if (video == null) throw new InvalidOperationException("No playable stream was found for this link.");

        var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (long)(d.GetDouble() * 1000) : 0;
        var isLive = root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True;
        return new ResolvedStream(video, audio, Str(root, "title") ?? "Online video", Str(root, "uploader") ?? Str(root, "channel"), duration, isLive);
    }

    private static string CleanError(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:")) ?? stderr.Trim();
        line = line.Replace("ERROR: ", "");
        var bracket = line.IndexOf("] ", StringComparison.Ordinal);
        if (line.StartsWith('[') && bracket > 0) line = line[(bracket + 2)..];
        return line.Length > 160 ? line[..160] + "…" : line.Length == 0 ? "The link couldn't be opened." : line;
    }

    /// <summary>Runs yt-dlp's self-updater. Returns its last line of output.</summary>
    public static async Task<string> UpdateAsync()
    {
        var psi = new ProcessStartInfo(ToolPath, $"{NoCertifi[0]} {NoCertifi[1]} -U")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start yt-dlp.");
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "Done";
    }
}
