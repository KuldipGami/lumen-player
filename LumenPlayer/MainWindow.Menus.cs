using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LibVLCSharp.Shared;
using LumenPlayer.Models;

namespace LumenPlayer;

// Menus, track selection, video geometry and information sheets.
public partial class MainWindow
{
    private static readonly (string Label, string? Value)[] AspectRatios =
    {
        ("Default", null), ("16:9", "16:9"), ("4:3", "4:3"), ("21:9", "21:9"), ("16:10", "16:10"),
        ("1:1", "1:1"), ("5:4", "5:4"), ("2.35:1", "235:100"), ("1.85:1", "185:100"),
    };

    private static readonly (string Label, string? Value)[] CropRatios =
    {
        ("None", null), ("16:9", "16:9"), ("4:3", "4:3"), ("16:10", "16:10"), ("1.85:1", "185:100"),
        ("2.21:1", "221:100"), ("2.35:1", "235:100"), ("2.39:1", "239:100"), ("1:1", "1:1"),
    };

    private static readonly (string Label, float Value)[] ZoomLevels =
    {
        ("Fit to window", 0f), ("25%", 0.25f), ("50%", 0.5f), ("100% (original)", 1f), ("200%", 2f),
    };

    private int _aspectIndex, _cropIndex, _zoomIndex;

    // ===================== Menu helpers =====================

    private static MenuItem Item(string header, Action action, string? glyph = null, string? gesture = null,
        bool isChecked = false, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = glyph,
            InputGestureText = gesture ?? "",
            IsChecked = isChecked,
            IsEnabled = enabled,
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Sub(string header, string? glyph, IEnumerable<object> children, bool enabled = true)
    {
        var item = new MenuItem { Header = header, Icon = glyph, IsEnabled = enabled };
        foreach (var child in children) item.Items.Add(child);
        if (item.Items.Count == 0)
        {
            item.Items.Add(new MenuItem { Header = "Nothing available", IsEnabled = false });
        }
        return item;
    }

    private void ShowMenu(ContextMenu menu, UIElement? target, PlacementMode placement = PlacementMode.Top)
    {
        menu.PlacementTarget = target;
        menu.Placement = target == null ? PlacementMode.MousePoint : placement;
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            ShowControls();
        };
        menu.IsOpen = true;
    }

    private bool HasMedia => _current != null && IsPlaybackActive(_player.State);

    // ===================== Main menu =====================

    private ContextMenu BuildMainMenu()
    {
        var menu = new ContextMenu();
        var media = HasMedia;

        menu.Items.Add(Item("Open file…", () => OpenFiles(), "", "Ctrl+O"));
        menu.Items.Add(Item("Open folder…", () => OpenFolder(), "", "Ctrl+F"));
        menu.Items.Add(Item("Open link (YouTube, stream)…", () => OpenUrl(), "", "Ctrl+N"));
        var recent = _settings.RecentFiles.Take(10)
            .Select(p => (object)Item(IsNetworkUrl(p) ? p : Path.GetFileName(p), () => OpenRecent(p)))
            .ToList();
        if (recent.Count > 0)
        {
            recent.Add(new Separator());
            recent.Add(Item("Clear recent", () => { _settings.RecentFiles.Clear(); RefreshRecent(); }));
        }
        menu.Items.Add(Sub("Recent media", "", recent));
        menu.Items.Add(new Separator());

        menu.Items.Add(Sub("Playback", "", new object[]
        {
            Item(_player.IsPlaying ? "Pause" : "Play", TogglePlay, _player.IsPlaying ? "" : "", "Space"),
            Item("Stop", StopPlayback, "", "S", enabled: media),
            Item("Previous", PlayPrevious, "", "P"),
            Item("Next", () => PlayNext(), "", "N"),
            new Separator(),
            Item($"Jump back {MediaFormats.FormatJump(_settings.Jump)}", () => SeekBy(-Ms(_settings.Jump)), "", "Ctrl+←", enabled: media),
            Item($"Jump forward {MediaFormats.FormatJump(_settings.Jump)}", () => SeekBy(Ms(_settings.Jump)), "", "Ctrl+→", enabled: media),
            Item("Next frame", () => _player.NextFrame(), "", "E", enabled: media),
            Sub("Speed", "", BuildSpeedItems()),
            new Separator(),
            Item("A-B loop", ToggleAbLoop, null, "Shift+L", isChecked: _abStart >= 0, enabled: media),
            Item("Shuffle", ToggleShuffle, null, "R", isChecked: _settings.Shuffle),
            Sub("Repeat", "", new object[]
            {
                Item("Off", () => SetRepeat(RepeatMode.Off), isChecked: _settings.Repeat == RepeatMode.Off),
                Item("Repeat all", () => SetRepeat(RepeatMode.All), isChecked: _settings.Repeat == RepeatMode.All),
                Item("Repeat one", () => SetRepeat(RepeatMode.One), isChecked: _settings.Repeat == RepeatMode.One),
            }),
        }));
        menu.Items.Add(Sub("Audio", "", BuildAudioItems()));
        menu.Items.Add(Sub("Video", "", BuildVideoItems()));
        menu.Items.Add(Sub("Subtitles", "", BuildSubtitleItems()));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("Adjustments & effects", ToggleEffects, "", "Ctrl+E"));
        menu.Items.Add(Item("Playlist", TogglePlaylist, "", "Ctrl+L"));
        menu.Items.Add(Item("Media information", ShowMediaInfo, "", "Ctrl+I", enabled: media));
        menu.Items.Add(Item("Fullscreen", ToggleFullscreen, "", "F", isChecked: _isFullscreen));
        menu.Items.Add(Item("Always on top", ToggleTopmost, null, "Ctrl+T", isChecked: Topmost));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", ShowSettings, "", "Ctrl+P"));
        menu.Items.Add(Item("Keyboard shortcuts", ShowShortcuts, "", "F1"));
        menu.Items.Add(Item("Exit", Close, "", "Ctrl+Q"));
        return menu;
    }

    private void SetRepeat(RepeatMode mode)
    {
        _settings.Repeat = mode;
        UpdateToggleButtons();
    }

    // ===================== Speed =====================

    private IEnumerable<object> BuildSpeedItems() =>
        SpeedSteps.Select(s => (object)Item(
            Math.Abs(s - 1) < 0.001 ? "Normal" : s.ToString("0.##", CultureInfo.InvariantCulture) + "×",
            () => SetRate((float)s),
            isChecked: Math.Abs(s - _rate) < 0.001));

    private ContextMenu BuildSpeedMenu()
    {
        var menu = new ContextMenu();
        foreach (var item in BuildSpeedItems()) menu.Items.Add(item);
        return menu;
    }

    // ===================== Audio =====================

    private IEnumerable<object> BuildAudioItems()
    {
        var items = new List<object>();
        var tracks = _player.AudioTrackDescription;
        var current = _player.AudioTrack;
        foreach (var t in tracks)
        {
            var id = t.Id;
            items.Add(Item(t.Name ?? $"Track {id}", () => _player.SetAudioTrack(id), isChecked: id == current));
        }
        if (items.Count == 0) items.Add(new MenuItem { Header = "No audio tracks", IsEnabled = false });

        items.Add(new Separator());
        var devices = _player.AudioOutputDeviceEnum;
        var currentDevice = _player.OutputDevice;
        var deviceItems = new List<object>
        {
            Item("System default", () => _player.SetOutputDevice(""), isChecked: string.IsNullOrEmpty(currentDevice)),
        };
        foreach (var d in devices)
        {
            var id = d.DeviceIdentifier;
            if (string.IsNullOrEmpty(id)) continue;
            deviceItems.Add(Item(d.Description ?? id, () => _player.SetOutputDevice(id), isChecked: id == currentDevice));
        }
        items.Add(Sub("Output device", "", deviceItems));
        items.Add(Sub("Delay", "", new object[]
        {
            Item("Delay −50 ms", () => NudgeAudioDelay(-50), null, "J"),
            Item("Delay +50 ms", () => NudgeAudioDelay(50), null, "K"),
            Item("Reset delay", () => { if (_audioDelaySlider != null) _audioDelaySlider.Value = 0; }),
        }));
        items.Add(Item(_settings.Muted ? "Unmute" : "Mute", ToggleMute, "", "M"));
        items.Add(Item("Volume up", () => ChangeVolume(_settings.VolumeStep), "", "↑"));
        items.Add(Item("Volume down", () => ChangeVolume(-_settings.VolumeStep), "", "↓"));
        return items;
    }

    private ContextMenu BuildAudioMenu()
    {
        var menu = new ContextMenu();
        foreach (var item in BuildAudioItems()) menu.Items.Add(item);
        return menu;
    }

    private void CycleAudioTrack()
    {
        var tracks = _player.AudioTrackDescription.Where(t => t.Id >= 0).ToArray();
        if (tracks.Length == 0)
        {
            ShowOsd("No audio tracks");
            return;
        }
        var index = Array.FindIndex(tracks, t => t.Id == _player.AudioTrack);
        var next = tracks[(index + 1) % tracks.Length];
        _player.SetAudioTrack(next.Id);
        ShowOsd($"Audio: {next.Name}");
    }

    private void NudgeAudioDelay(int ms)
    {
        if (_audioDelaySlider == null) return;
        _audioDelaySlider.Value = Math.Clamp(_audioDelaySlider.Value + ms, _audioDelaySlider.Minimum, _audioDelaySlider.Maximum);
        ShowOsd($"Audio delay {_audioDelaySlider.Value:+0;−0;0} ms");
    }

    // ===================== Subtitles =====================

    private IEnumerable<object> BuildSubtitleItems()
    {
        var items = new List<object>();
        var current = _player.Spu;
        foreach (var t in _player.SpuDescription)
        {
            var id = t.Id;
            var name = id < 0 ? "Off" : t.Name ?? $"Track {id}";
            items.Add(Item(name, () => _player.SetSpu(id), isChecked: id == current));
        }
        if (items.Count == 0) items.Add(new MenuItem { Header = "No subtitle tracks", IsEnabled = false });
        items.Add(new Separator());
        items.Add(Item("Load subtitle file…", BrowseSubtitle, "", enabled: HasMedia));
        items.Add(Item("Delay −50 ms", () => NudgeSubtitleDelay(-50), null, "G"));
        items.Add(Item("Delay +50 ms", () => NudgeSubtitleDelay(50), null, "H"));
        items.Add(Item("Reset delay", () => { if (_subDelaySlider != null) _subDelaySlider.Value = 0; }));
        return items;
    }

    private ContextMenu BuildSubtitleMenu()
    {
        var menu = new ContextMenu();
        foreach (var item in BuildSubtitleItems()) menu.Items.Add(item);
        return menu;
    }

    private void BrowseSubtitle()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = MediaFormats.SubtitleFilter, Title = "Load subtitles" };
        if (_current is { IsUrl: false }) dlg.InitialDirectory = Path.GetDirectoryName(_current.Path);
        if (dlg.ShowDialog(this) == true) LoadSubtitle(dlg.FileName);
    }

    private void CycleSubtitle()
    {
        var tracks = _player.SpuDescription;
        if (tracks.Length == 0)
        {
            ShowOsd("No subtitles");
            return;
        }
        var index = Array.FindIndex(tracks, t => t.Id == _player.Spu);
        var next = tracks[(index + 1) % tracks.Length];
        _player.SetSpu(next.Id);
        ShowOsd(next.Id < 0 ? "Subtitles off" : $"Subtitles: {next.Name}");
    }

    private void NudgeSubtitleDelay(int ms)
    {
        if (_subDelaySlider == null) return;
        _subDelaySlider.Value = Math.Clamp(_subDelaySlider.Value + ms, _subDelaySlider.Minimum, _subDelaySlider.Maximum);
        ShowOsd($"Subtitle delay {_subDelaySlider.Value:+0;−0;0} ms");
    }

    // ===================== Video geometry =====================

    private IEnumerable<object> BuildVideoItems()
    {
        var items = new List<object>
        {
            Sub("Aspect ratio", "", AspectRatios.Select((a, i) => (object)Item(a.Label, () => SetAspect(i), isChecked: i == _aspectIndex))),
            Sub("Crop", "", CropRatios.Select((c, i) => (object)Item(c.Label, () => SetCrop(i), isChecked: i == _cropIndex))),
            Sub("Zoom", "", ZoomLevels.Select((z, i) => (object)Item(z.Label, () => SetZoom(i), isChecked: i == _zoomIndex))),
            Sub("Deinterlace", "", new object[]
            {
                Item("Off", () => SetDeinterlace(null), isChecked: _deinterlace == null),
                Item("Blend", () => SetDeinterlace("blend"), isChecked: _deinterlace == "blend"),
                Item("Bob", () => SetDeinterlace("bob"), isChecked: _deinterlace == "bob"),
                Item("Linear", () => SetDeinterlace("linear"), isChecked: _deinterlace == "linear"),
                Item("Yadif", () => SetDeinterlace("yadif"), isChecked: _deinterlace == "yadif"),
                Item("Yadif (2x)", () => SetDeinterlace("yadif2x"), isChecked: _deinterlace == "yadif2x"),
            }),
            new Separator(),
            Item("Take snapshot", TakeSnapshot, "", "Shift+S", enabled: HasMedia),
            Item("Fullscreen", ToggleFullscreen, "", "F", isChecked: _isFullscreen),
        };
        return items;
    }

    private ContextMenu BuildVideoMenu()
    {
        var menu = new ContextMenu();
        foreach (var item in BuildVideoItems()) menu.Items.Add(item);
        return menu;
    }

    private string? _deinterlace;

    private void SetDeinterlace(string? mode)
    {
        _deinterlace = mode;
        _player.SetDeinterlace(mode ?? "");
        ShowOsd(mode == null ? "Deinterlace off" : $"Deinterlace: {mode}");
    }

    private void SetAspect(int index)
    {
        _aspectIndex = index;
        _player.AspectRatio = AspectRatios[index].Value;
        ShowOsd($"Aspect ratio: {AspectRatios[index].Label}");
    }

    private void SetCrop(int index)
    {
        _cropIndex = index;
        _player.CropGeometry = CropRatios[index].Value;
        ShowOsd($"Crop: {CropRatios[index].Label}");
    }

    private void SetZoom(int index)
    {
        _zoomIndex = index;
        _player.Scale = ZoomLevels[index].Value;
        ShowOsd($"Zoom: {ZoomLevels[index].Label}");
    }

    private void CycleAspect() => SetAspect((_aspectIndex + 1) % AspectRatios.Length);
    private void CycleCrop() => SetCrop((_cropIndex + 1) % CropRatios.Length);
    private void CycleZoom() => SetZoom((_zoomIndex + 1) % ZoomLevels.Length);

    // ===================== Information sheets =====================

    private void ShowMediaInfo()
    {
        var media = _player.Media;
        if (_current == null || media == null || !IsPlaybackActive(_player.State))
        {
            ShowOsd("Nothing is playing");
            return;
        }

        var panel = new StackPanel { Width = 540 };
        void Section(string title) => panel.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Style = (Style)FindResource("SectionLabel"),
            Foreground = (Brush)FindResource("Accent"),
            Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 18, 0, 6),
        });
        void Row(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new TextBlock { Text = key, Foreground = (Brush)FindResource("TextSecondary"), FontSize = 13 });
            var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            grid.Children.Add(v);
            panel.Children.Add(grid);
        }

        Section("General");
        // Online videos: LibVLC only sees the raw stream URL, so use what yt-dlp reported instead.
        var online = _current.Resolved;
        Row("Title", online != null ? _current.Title : media.Meta(MetadataType.Title) ?? _current.Title);
        Row("Channel", online?.Uploader);
        Row("Artist", online != null ? null : media.Meta(MetadataType.Artist));
        Row("Album", media.Meta(MetadataType.Album));
        Row("Genre", media.Meta(MetadataType.Genre));
        Row("Date", media.Meta(MetadataType.Date));
        Row("Now playing", media.Meta(MetadataType.NowPlaying));
        Row("Location", _current.Path);
        if (!_current.IsUrl && File.Exists(_current.Path)) Row("File size", FormatSize(new FileInfo(_current.Path).Length));
        if (_player.Length > 0) Row("Duration", MediaFormats.FormatTime(_player.Length));

        int v = 0, a = 0, s = 0;
        foreach (var t in media.Tracks)
        {
            switch (t.TrackType)
            {
                case TrackType.Video:
                    Section(++v == 1 ? "Video" : $"Video {v}");
                    Row("Codec", CodecName(t));
                    Row("Resolution", $"{t.Data.Video.Width} × {t.Data.Video.Height}");
                    if (t.Data.Video.FrameRateDen > 0)
                        Row("Frame rate", $"{t.Data.Video.FrameRateNum / (double)t.Data.Video.FrameRateDen:0.###} fps");
                    break;
                case TrackType.Audio:
                    Section(++a == 1 ? "Audio" : $"Audio {a}");
                    Row("Codec", CodecName(t));
                    Row("Channels", t.Data.Audio.Channels switch { 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", var c => $"{c}" });
                    Row("Sample rate", $"{t.Data.Audio.Rate:N0} Hz");
                    break;
                case TrackType.Text:
                    Section(++s == 1 ? "Subtitles" : $"Subtitles {s}");
                    Row("Codec", CodecName(t));
                    break;
                default:
                    continue;
            }
            if (t.Bitrate > 0) Row("Bitrate", $"{t.Bitrate / 1000:N0} kb/s");
            Row("Language", t.Language);
            Row("Description", t.Description);
        }

        DialogWindow.ShowSheet(this, "Media information", panel);
    }

    private string CodecName(MediaTrack track)
    {
        var c = track.Codec;
        var fourcc = new string(new[] { (char)(c & 0xFF), (char)((c >> 8) & 0xFF), (char)((c >> 16) & 0xFF), (char)((c >> 24) & 0xFF) }).Trim();
        string? description = null;
        try { description = _player.Media?.CodecDescription(track.TrackType, c); } catch { /* optional */ }
        return string.IsNullOrWhiteSpace(description) ? fourcc : $"{description} ({fourcc})";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    private void ShowShortcuts()
    {
        var groups = new (string Group, (string Keys, string Action)[] Items)[]
        {
            ("Playback", new[]
            {
                ("Space", "Play / pause"), ("S", "Stop"), ("N  /  P", "Next / previous"),
                ("←  /  →", $"Seek {J(_settings.ShortJump)}  (Shift: {J(_settings.VeryShortJump)}, Ctrl: {J(_settings.Jump)}, Alt: {J(_settings.LongJump)})"),
                ("0 – 9", "Jump to 0 – 90%"),
                ("[  /  ]", "Slower / faster"), ("Backspace", "Normal speed"), ("E", "Next frame"),
                ("Shift+L", "A-B loop"), ("L", "Repeat mode"), ("R", "Shuffle"), ("T", "Show time"),
            }),
            ("Audio & subtitles", new[]
            {
                ("↑  /  ↓  or wheel", "Volume"), ("M", "Mute"), ("B", "Next audio track"), ("V", "Next subtitle track"),
                ("G  /  H", "Subtitle delay −/+ 50 ms"), ("J  /  K", "Audio delay −/+ 50 ms"),
            }),
            ("Video & view", new[]
            {
                ("F  /  double-click", "Fullscreen"), ("Esc", "Leave fullscreen"), ("A", "Cycle aspect ratio"),
                ("C", "Cycle crop"), ("Z", "Cycle zoom"), ("Shift+S", "Snapshot"), ("Ctrl+E", "Adjustments & effects"),
                ("Ctrl+L", "Playlist"), ("Ctrl+I", "Media information"), ("Ctrl+T", "Always on top"),
            }),
            ("Open", new[]
            {
                ("Ctrl+O", "Open file"), ("Ctrl+F", "Open folder"), ("Ctrl+N", "Open link (YouTube, stream)"), ("Ctrl+S", "Save playlist"),
                ("Ctrl+P", "Settings (jump lengths, volume step)"),
            }),
        };
        static string J(double seconds) => MediaFormats.FormatJump(seconds);

        var panel = new StackPanel { Width = 520 };
        foreach (var (group, rows) in groups)
        {
            panel.Children.Add(new TextBlock
            {
                Text = group.ToUpperInvariant(),
                Style = (Style)FindResource("SectionLabel"),
                Foreground = (Brush)FindResource("Accent"),
                Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 16, 0, 6),
            });
            foreach (var (keys, action) in rows)
            {
                var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(new Border
                {
                    Background = (Brush)FindResource("Surface2"),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = keys, FontSize = 12, FontWeight = FontWeights.SemiBold },
                });
                var text = new TextBlock { Text = action, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                panel.Children.Add(grid);
            }
        }
        DialogWindow.ShowSheet(this, "Keyboard shortcuts", panel);
    }
}
