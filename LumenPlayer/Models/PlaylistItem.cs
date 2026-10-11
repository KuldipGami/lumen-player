using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace LumenPlayer.Models;

public class PlaylistItem : INotifyPropertyChanged
{
    private bool _isCurrent;
    private string _duration = "";

    public PlaylistItem(string path)
    {
        Path = path;
        IsUrl = Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;
        if (IsUrl)
        {
            var host = uri!.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            Title = StreamResolver.IsKnownVideoSite(path)
                ? $"Video on {host}" // replaced by the real title once yt-dlp resolves it
                : Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.Trim('/') ?? "");
            if (string.IsNullOrWhiteSpace(Title)) Title = host;
            Subtitle = host;
        }
        else
        {
            Title = System.IO.Path.GetFileNameWithoutExtension(path);
            Subtitle = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
        }
    }

    private string _title = "";
    private string _subtitle = "";

    public string Path { get; }
    public bool IsUrl { get; }
    public bool IsAudio => !IsUrl && MediaFormats.IsAudio(Path);
    public string Glyph => IsUrl ? "" : IsAudio ? "" : "";

    public string Title
    {
        get => _title;
        set { _title = value; OnChanged(); }
    }

    public string Subtitle
    {
        get => _subtitle;
        set { _subtitle = value; OnChanged(); }
    }

    /// <summary>Stream URLs from yt-dlp. They expire after a few hours, so they're re-resolved when stale.</summary>
    public ResolvedStream? Resolved { get; set; }
    public DateTime ResolvedAt { get; set; }
    public bool NeedsResolving => IsUrl && StreamResolver.NeedsResolving(Path);

    public bool IsCurrent
    {
        get => _isCurrent;
        set { _isCurrent = value; OnChanged(); }
    }

    public string Duration
    {
        get => _duration;
        set { _duration = value; OnChanged(); }
    }

    public bool DurationProbed { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public record RecentEntry(string Path, string Name, string Folder, string Glyph);
