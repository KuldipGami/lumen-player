using System.IO;
using System.Runtime.InteropServices;

namespace LumenPlayer.Models;

public static class MediaFormats
{
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv", ".f4v", ".mpg", ".mpeg", ".m2v",
        ".m2ts", ".mts", ".ts", ".vob", ".3gp", ".3g2", ".ogv", ".divx", ".xvid", ".rm", ".rmvb", ".asf",
        ".hevc", ".h264", ".h265", ".mxf", ".dv", ".iso", ".y4m", ".av1", ".ivf",
    };

    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".oga", ".opus", ".wma", ".ape", ".alac", ".aiff",
        ".aif", ".mka", ".ac3", ".eac3", ".dts", ".mpc", ".wv", ".amr", ".mid", ".midi", ".tta",
    };

    public static readonly HashSet<string> Playlists = new(StringComparer.OrdinalIgnoreCase) { ".m3u", ".m3u8", ".pls" };

    public static readonly HashSet<string> Subtitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".smi", ".sami", ".txt", ".usf", ".jss",
    };

    public static bool IsAudio(string path) => Audio.Contains(Path.GetExtension(path));
    public static bool IsMedia(string path)
    {
        var ext = Path.GetExtension(path);
        return Video.Contains(ext) || Audio.Contains(ext);
    }
    public static bool IsPlaylist(string path) => Playlists.Contains(Path.GetExtension(path));
    public static bool IsSubtitle(string path) => Subtitles.Contains(Path.GetExtension(path)) && !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    public static string DialogFilter
    {
        get
        {
            static string Join(IEnumerable<string> exts) => string.Join(";", exts.Select(e => "*" + e));
            return $"Media files|{Join(Video.Concat(Audio).Concat(Playlists))}|Video|{Join(Video)}|Audio|{Join(Audio)}|Playlists|{Join(Playlists)}|All files|*.*";
        }
    }

    public static string SubtitleFilter => $"Subtitles|{string.Join(";", Subtitles.Select(e => "*" + e))}|All files|*.*";

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Explorer-style "natural" ordering, so "Ep 2" sorts before "Ep 10".</summary>
    public static readonly Comparer<string?> NaturalOrder = Comparer<string?>.Create((a, b) => StrCmpLogicalW(a ?? "", b ?? ""));

    public static IEnumerable<string> EnumerateFolder(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(IsMedia).ToList();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
        return files.OrderBy(Path.GetDirectoryName, NaturalOrder).ThenBy(Path.GetFileName, NaturalOrder);
    }

    public static IEnumerable<string> ReadPlaylist(string file)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var isPls = file.EndsWith(".pls", StringComparison.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (isPls)
            {
                if (!line.StartsWith("File", StringComparison.OrdinalIgnoreCase)) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                line = line[(eq + 1)..].Trim();
            }
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && !uri.IsFile) yield return line;
            else if (uri is { IsFile: true }) yield return uri.LocalPath;
            else yield return Path.GetFullPath(Path.Combine(dir, line));
        }
    }

    /// <summary>"1 s", "2.5 s", "1 min", "1 min 30 s".</summary>
    public static string FormatJump(double seconds)
    {
        if (seconds < 60) return $"{seconds.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)} s";
        var min = (int)(seconds / 60);
        var sec = seconds - min * 60;
        return sec == 0 ? $"{min} min" : $"{min} min {sec.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)} s";
    }

    public static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
