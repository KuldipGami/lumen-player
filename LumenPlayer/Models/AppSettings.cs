using System.IO;
using System.Text.Json;

namespace LumenPlayer.Models;

public enum RepeatMode { Off, All, One }

/// <summary>Persisted user preferences, stored as JSON in %APPDATA%\LumenPlayer.</summary>
public class AppSettings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumenPlayer");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;
    public bool ShowPlaylist { get; set; }
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 720;
    public bool WindowMaximized { get; set; }

    // Seek distances in seconds (VLC calls these very short / short / medium / long jumps).
    public double VeryShortJump { get; set; } = 1;
    public double ShortJump { get; set; } = 5;
    public double Jump { get; set; } = 10;
    public double LongJump { get; set; } = 60;
    public int VolumeStep { get; set; } = 5;

    public bool EqualizerEnabled { get; set; }
    public float Preamp { get; set; }
    public float[] EqualizerBands { get; set; } = Array.Empty<float>();
    public string EqualizerPreset { get; set; } = "Flat";

    public List<string> RecentFiles { get; set; } = new();
    public Dictionary<string, long> ResumePositions { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings: start fresh rather than crash.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal: preferences just won't persist this time.
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
    }
}
