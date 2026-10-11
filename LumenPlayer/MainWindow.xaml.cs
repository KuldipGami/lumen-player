using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LumenPlayer.Models;
using Microsoft.Win32;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace LumenPlayer;

public partial class MainWindow : Window
{
    private const string GlyphPlay = "";
    private const string GlyphPause = "";

    private readonly LibVLC _libVlc;
    private readonly VlcPlayer _player;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<PlaylistItem> _playlist = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(1.6) };
    private readonly Random _random = new();
    private readonly string[] _startupArgs;

    private PlaylistItem? _current;
    private bool _isSeeking;
    private DateTime _lastLiveSeek;
    private bool _showRemaining;
    private long _pendingResume;
    private float _rate = 1f;
    private long _abStart = -1, _abEnd = -1;
    private bool _probingDurations;
    private bool _audioPanelChecked;
    private bool _hasVideo;
    private int _playRequest;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _startupArgs = args;

        _libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--no-stats", "--quiet");
        _player = new VlcPlayer(_libVlc)
        {
            EnableHardwareDecoding = true,
            EnableKeyInput = false,
            EnableMouseInput = false,
        };

        // LibVLC raises events on its own threads; never call back into the player from there.
        _player.Playing += (_, _) => OnUi(OnPlaying);
        _player.Paused += (_, _) => OnUi(UpdatePlayState);
        _player.Stopped += (_, _) => OnUi(UpdatePlayState);
        _player.EndReached += (_, _) => OnUi(OnEndReached);
        _player.EncounteredError += (_, _) => OnUi(OnPlaybackError);
        _player.Vout += (_, e) => OnUi(() => OnVideoOutput(e.Count));

        _uiTimer.Tick += (_, _) => UpdateTimeline();
        _hideTimer.Tick += (_, _) => AutoHideControls();
        _osdTimer.Tick += (_, _) => { _osdTimer.Stop(); Fade(Osd, 0, 350); };

        PlaylistBox.ItemsSource = _playlist;
        _playlist.CollectionChanged += (_, _) => UpdatePlaylistInfo();

        MakeScrubbable(SeekSlider, onStart: () => _isSeeking = true, onDrag: SeekPreview, onRelease: SeekCommit);
        MakeScrubbable(VolumeSlider);

        BuildEffectsPanel();
        ApplySettings();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VideoView.MediaPlayer = _player;
        PaintVideoHostBlack();

        ThumbPrev.ImageSource = GlyphImage("");
        ThumbNext.ImageSource = GlyphImage("");

        RefreshRecent();
        UpdatePlaylistInfo();
        UpdateSurface();
        UpdatePlayState();
        _uiTimer.Start();

        if (_startupArgs.Length > 0) AddPaths(_startupArgs, play: true);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int obj);

    [DllImport("gdi32.dll")]
    private static extern uint SetBkColor(IntPtr hdc, uint color);

    /// <summary>
    /// VideoView hosts the native video surface in a Win32 "Static" control, which asks its parent for a
    /// background brush and defaults to light gray. LibVLC doesn't paint letterbox bars or the surface
    /// before the first frame, so answer WM_CTLCOLORSTATIC with black.
    /// </summary>
    private void PaintVideoHostBlack()
    {
        const int WM_CTLCOLORSTATIC = 0x0138, BLACK_BRUSH = 4;
        var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        source?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WM_CTLCOLORSTATIC) return IntPtr.Zero;
            SetBkColor(wParam, 0);
            handled = true;
            return GetStockObject(BLACK_BRUSH);
        });
    }

    private void ApplySettings()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        VolumeSlider.Value = Math.Clamp(_settings.Volume, 0, 200);
        Topmost = _settings.AlwaysOnTop;
        Sidebar.Visibility = _settings.ShowPlaylist ? Visibility.Visible : Visibility.Collapsed;
        UpdateToggleButtons();
        UpdateMuteGlyph();
        ApplyJumpLabels();
    }

    private void OnUi(Action action) => Dispatcher.BeginInvoke(action);

    // ===================== Playlist & opening =====================

    private void OpenFiles(bool play = true)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = MediaFormats.DialogFilter, Title = "Open media" };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames, play);
    }

    private void OpenFolder(bool play = true)
    {
        var dlg = new OpenFolderDialog { Title = "Open folder", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FolderNames, play);
    }

    private void OpenUrl(bool play = true)
    {
        string initial = "";
        try
        {
            var clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (IsNetworkUrl(clip)) initial = clip;
        }
        catch
        {
            // Clipboard can be locked by another process; just start empty.
        }

        var url = DialogWindow.Prompt(this, "Open link",
            "Paste a YouTube, Vimeo, Twitch or other video page link — or a direct stream URL " +
            "(http, HLS .m3u8, DASH, RTSP, RTMP, UDP, FTP, internet radio).", initial, "Play");
        if (url == null) return;
        if (!IsNetworkUrl(url) && !File.Exists(url))
        {
            ShowOsd("That doesn't look like a valid URL");
            return;
        }
        AddPaths(new[] { url }, play);
    }

    private static bool IsNetworkUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1;

    private void AddPaths(IEnumerable<string> paths, bool play)
    {
        var added = new List<PlaylistItem>();
        foreach (var raw in paths)
        {
            var path = raw.Trim().Trim('"');
            if (path.Length == 0) continue;

            if (Directory.Exists(path))
                added.AddRange(MediaFormats.EnumerateFolder(path).Select(f => new PlaylistItem(f)));
            else if (File.Exists(path) && MediaFormats.IsPlaylist(path))
                added.AddRange(MediaFormats.ReadPlaylist(path).Select(f => new PlaylistItem(f)));
            else if (File.Exists(path) && MediaFormats.IsSubtitle(path))
                LoadSubtitle(path);
            else if (File.Exists(path) || IsNetworkUrl(path))
                added.Add(new PlaylistItem(path));
        }

        if (added.Count == 0) return;
        foreach (var item in added) _playlist.Add(item);

        if (play || _current == null || !IsPlaybackActive(_player.State)) PlayItem(added[0]);
        else ShowOsd(added.Count == 1 ? $"Added “{added[0].Title}”" : $"Added {added.Count} items");

        ProbeDurations();
    }

    private async void ProbeDurations()
    {
        if (_probingDurations) return;
        _probingDurations = true;
        try
        {
            while (_playlist.FirstOrDefault(i => !i.DurationProbed && !i.IsUrl) is { } item)
            {
                item.DurationProbed = true;
                try
                {
                    using var media = new VlcMedia(_libVlc, item.Path, FromType.FromPath);
                    var status = await media.Parse(MediaParseOptions.ParseLocal, 5000);
                    if (status == MediaParsedStatus.Done && media.Duration > 0 && string.IsNullOrEmpty(item.Duration))
                        item.Duration = MediaFormats.FormatTime(media.Duration);
                }
                catch
                {
                    // Unparseable files simply show no duration.
                }
            }
        }
        finally
        {
            _probingDurations = false;
        }
    }

    private void UpdatePlaylistInfo()
    {
        var n = _playlist.Count;
        PlaylistCount.Text = n == 0 ? "Empty" : n == 1 ? "1 item" : $"{n} items";
        PlaylistEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshRecent()
    {
        var entries = _settings.RecentFiles.Take(6).Select(p =>
        {
            var item = new PlaylistItem(p);
            return new RecentEntry(p, item.IsUrl ? p : Path.GetFileName(p), item.Subtitle, item.Glyph);
        }).ToList();
        RecentList.ItemsSource = entries;
        RecentSection.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenRecent(string path)
    {
        if (!IsNetworkUrl(path) && !File.Exists(path) && !Directory.Exists(path))
        {
            _settings.RecentFiles.Remove(path);
            RefreshRecent();
            ShowOsd("That file no longer exists");
            return;
        }
        AddPaths(new[] { path }, play: true);
    }

    // ===================== Playback =====================

    private static bool IsPlaybackActive(VLCState s) =>
        s is VLCState.Playing or VLCState.Paused or VLCState.Opening or VLCState.Buffering;

    private async void PlayItem(PlaylistItem item)
    {
        SaveResumePosition();
        if (_current != null) _current.IsCurrent = false;
        _current = item;
        item.IsCurrent = true;
        ClearAbLoop(silent: true);
        ResetSyncSliders();
        var request = ++_playRequest;

        _settings.AddRecent(item.Path);
        RefreshRecent();
        ShowNowPlaying(item);
        PlaylistBox.ScrollIntoView(item);

        WelcomePanel.Visibility = Visibility.Collapsed;
        _audioPanelChecked = false;
        ArtworkImage.Source = null;
        AudioPanel.Visibility = item.IsAudio ? Visibility.Visible : Visibility.Collapsed;
        _hasVideo = false;
        UpdateSurface();

        // Web pages (YouTube etc.) must be turned into direct stream URLs first. They expire, so refresh hourly.
        if (item.NeedsResolving && StreamResolver.IsAvailable &&
            (item.Resolved == null || DateTime.UtcNow - item.ResolvedAt > TimeSpan.FromHours(1)))
        {
            _player.Stop();
            ShowOsd("Loading video…", seconds: 60);
            try
            {
                item.Resolved = await StreamResolver.ResolveAsync(item.Path, CancellationToken.None);
                item.ResolvedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                if (request != _playRequest) return;
                item.Resolved = null;
                if (StreamResolver.IsKnownVideoSite(item.Path))
                {
                    ShowIdle();
                    ShowOsd($"Couldn't play this link — {ex.Message}", seconds: 8);
                    return;
                }
                // Not a site yt-dlp understands; it may still be a plain stream LibVLC can open directly.
            }
            if (request != _playRequest) return; // the user picked something else while we were loading
            Osd.Opacity = 0;
        }
        else if (item.NeedsResolving && !StreamResolver.IsAvailable && StreamResolver.IsKnownVideoSite(item.Path))
        {
            ShowIdle();
            ShowOsd("YouTube support (Tools\\yt-dlp.exe) is missing — reinstall Lumen Player", seconds: 8);
            return;
        }

        if (item.Resolved is { } resolved)
        {
            item.Title = resolved.Title;
            if (resolved.Uploader != null) item.Subtitle = resolved.Uploader;
            if (resolved.DurationMs > 0) item.Duration = MediaFormats.FormatTime(resolved.DurationMs);
            ShowNowPlaying(item);
        }
        StartMedia(item);
    }

    private void StartMedia(PlaylistItem item)
    {
        VlcMedia media;
        if (item.Resolved is { } stream)
        {
            media = new VlcMedia(_libVlc, stream.VideoUrl, FromType.FromLocation);
            if (stream.AudioUrl != null) media.AddOption(":input-slave=" + stream.AudioUrl);
        }
        else
        {
            media = item.IsUrl
                ? new VlcMedia(_libVlc, item.Path, FromType.FromLocation)
                : new VlcMedia(_libVlc, item.Path, FromType.FromPath);
        }

        using (media)
        {
            _pendingResume = 0;
            if (CanResume(item) && _settings.ResumePositions.TryGetValue(item.Path, out var resume) && resume > 0)
            {
                media.AddOption(":start-time=" + (resume / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
                _pendingResume = resume;
            }
            _player.Play(media);
        }
    }

    /// <summary>Local files and on-demand online videos remember their position; live streams don't.</summary>
    private static bool CanResume(PlaylistItem item) => !item.IsUrl || item.Resolved is { IsLive: false };

    private void ShowNowPlaying(PlaylistItem item)
    {
        Title = $"{item.Title} — Lumen Player";
        TitleText.Text = item.Title;
        TopTitle.Text = item.Title;
        NowPlayingText.Text = item.Title;
        AudioTitle.Text = item.Title;
        AudioArtist.Text = item.IsUrl ? item.Subtitle : "";
    }

    /// <summary>
    /// The overlay sits in a transparent window above the native video surface, which is unpainted
    /// until LibVLC renders a frame. Keep the overlay opaque whenever there's no frame to show.
    /// </summary>
    private void UpdateSurface()
    {
        var showVideo = _hasVideo && WelcomePanel.Visibility != Visibility.Visible && AudioPanel.Visibility != Visibility.Visible;
        OverlayRoot.Background = showVideo
            ? SeeThrough
            : WelcomePanel.Visibility == Visibility.Visible || AudioPanel.Visibility == Visibility.Visible
                ? (Brush)FindResource("Bg")
                : Brushes.Black;
    }

    // Nearly transparent rather than transparent, so the layered overlay still receives mouse input.
    private static readonly Brush SeeThrough = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

    private void OnPlaying()
    {
        // Audio output only exists once playback starts, so re-apply audio state here.
        _player.Volume = (int)Math.Round(VolumeSlider.Value);
        _player.Mute = _settings.Muted;
        if (Math.Abs(_rate - 1f) > 0.001f) _player.SetRate(_rate);
        ApplyEqualizer();

        if (_pendingResume > 0)
        {
            ShowOsd($"Resumed at {MediaFormats.FormatTime(_pendingResume)}");
            _pendingResume = 0;
        }

        LoadMetadata();
        UpdatePlayState();
    }

    private void OnVideoOutput(int count)
    {
        _hasVideo = count > 0;
        if (_hasVideo)
        {
            AudioPanel.Visibility = Visibility.Collapsed;
            _audioPanelChecked = true;
            ApplyVideoAdjust();
        }
        UpdateSurface();
    }

    private void LoadMetadata()
    {
        var media = _player.Media;
        if (media == null || _current == null) return;

        var title = media.Meta(MetadataType.Title);
        var artist = media.Meta(MetadataType.Artist);
        var album = media.Meta(MetadataType.Album);
        if (!string.IsNullOrWhiteSpace(title) && !_current.IsUrl && _current.IsAudio) AudioTitle.Text = title;
        var sub = string.Join("  ·  ", new[] { artist, album }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (sub.Length > 0) AudioArtist.Text = sub;

        var art = media.Meta(MetadataType.ArtworkURL);
        if (!string.IsNullOrEmpty(art) && Uri.TryCreate(art, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.DecodePixelWidth = 440;
                bmp.EndInit();
                ArtworkImage.Source = bmp;
            }
            catch
            {
                ArtworkImage.Source = null;
            }
        }
    }

    private void OnPlaybackError()
    {
        var title = _current?.Title ?? "this media";
        ShowIdle();
        ShowOsd($"Couldn't play “{title}”", seconds: 5);
    }

    private void OnEndReached()
    {
        if (_current != null) _settings.ResumePositions.Remove(_current.Path);
        if (!PlayNext(fromEnd: true)) ShowIdle();
    }

    private bool PlayNext(bool fromEnd = false)
    {
        if (_playlist.Count == 0) return false;
        if (fromEnd && _settings.Repeat == RepeatMode.One && _current != null)
        {
            PlayItem(_current);
            return true;
        }

        var index = _current == null ? -1 : _playlist.IndexOf(_current);
        int next;
        if (_settings.Shuffle && _playlist.Count > 1)
        {
            do next = _random.Next(_playlist.Count); while (next == index);
        }
        else next = index + 1;

        if (next >= _playlist.Count)
        {
            if (_settings.Repeat != RepeatMode.All)
            {
                if (!fromEnd) ShowOsd("End of playlist");
                return false;
            }
            next = 0;
        }
        PlayItem(_playlist[next]);
        return true;
    }

    private void PlayPrevious()
    {
        if (_player.Time > 3000 && _player.IsSeekable)
        {
            _player.Time = 0;
            return;
        }
        if (_playlist.Count == 0) return;
        var index = _current == null ? 0 : _playlist.IndexOf(_current) - 1;
        if (index < 0)
        {
            if (_settings.Repeat != RepeatMode.All) index = 0;
            else index = _playlist.Count - 1;
        }
        PlayItem(_playlist[index]);
    }

    private void TogglePlay()
    {
        if (_current == null)
        {
            if (_playlist.Count > 0) PlayItem(_playlist[0]);
            else OpenFiles();
            return;
        }

        if (!IsPlaybackActive(_player.State))
        {
            PlayItem(_current);
            return;
        }

        var wasPlaying = _player.IsPlaying;
        if (wasPlaying && !_player.CanPause) return;
        _player.SetPause(wasPlaying);
        Flash(wasPlaying ? GlyphPause : GlyphPlay);
    }

    private void StopPlayback()
    {
        SaveResumePosition();
        _playRequest++; // abandon any link that's still loading
        _player.Stop();
        ShowIdle();
        Osd.Opacity = 0;
    }

    private void ShowIdle()
    {
        WelcomePanel.Visibility = Visibility.Visible;
        AudioPanel.Visibility = Visibility.Collapsed;
        Title = "Lumen Player";
        TitleText.Text = "Lumen Player";
        NowPlayingText.Text = "";
        SeekSlider.Maximum = 1;
        SeekSlider.Value = 0;
        TimeText.Text = LengthText.Text = "0:00";
        _hasVideo = false;
        UpdateSurface();
        UpdatePlayState();
    }

    private void UpdatePlayState()
    {
        var playing = _player.IsPlaying;
        PlayPauseBtn.Content = playing ? GlyphPause : GlyphPlay;
        ThumbPlay.ImageSource = GlyphImage(playing ? GlyphPause : GlyphPlay);
        ThumbPlay.Description = playing ? "Pause" : "Play";
        KeepAwake(playing);
        if (!playing) ShowControls();
        else _hideTimer.Start();
    }

    private void SaveResumePosition()
    {
        if (_current == null || !CanResume(_current)) return;
        long length = _player.Length, time = _player.Time;
        if (length < 60_000 || time <= 0) return;

        if (time > 15_000 && time < length - 20_000) _settings.ResumePositions[_current.Path] = time;
        else _settings.ResumePositions.Remove(_current.Path);

        while (_settings.ResumePositions.Count > 300)
            _settings.ResumePositions.Remove(_settings.ResumePositions.Keys.First());
    }

    // ===================== Timeline & seeking =====================

    private void UpdateTimeline()
    {
        long length = _player.Length, time = Math.Max(0, _player.Time);

        if (length > 0)
        {
            if (Math.Abs(SeekSlider.Maximum - length) > 0.5) SeekSlider.Maximum = length;
            if (!_isSeeking) SeekSlider.Value = Math.Min(time, length);
        }
        else if (!_isSeeking)
        {
            SeekSlider.Maximum = 1;
            SeekSlider.Value = 0;
        }

        var shown = _isSeeking ? (long)SeekSlider.Value : time;
        if (IsPlaybackActive(_player.State) || _isSeeking)
        {
            TimeText.Text = MediaFormats.FormatTime(shown);
            LengthText.Text = length <= 0
                ? (_player.State is VLCState.Opening or VLCState.Buffering ? "…" : _current?.IsUrl == true ? "LIVE" : "0:00")
                : _showRemaining ? "−" + MediaFormats.FormatTime(length - shown) : MediaFormats.FormatTime(length);
        }

        if (_current != null && length > 0 && string.IsNullOrEmpty(_current.Duration))
            _current.Duration = MediaFormats.FormatTime(length);

        // A-B repeat
        if (_abEnd > 0 && _player.IsPlaying && time >= _abEnd) _player.Time = _abStart;
        UpdateAbMarkers();

        if (TopBar.Visibility == Visibility.Visible) ClockText.Text = DateTime.Now.ToString("t");

        // Media with no video stream (radio, audio in a video container): show the audio card.
        if (!_audioPanelChecked && _player.IsPlaying && time > 1200)
        {
            _audioPanelChecked = true;
            if (_player.VideoTrackCount <= 0) AudioPanel.Visibility = Visibility.Visible;
            UpdateSurface();
        }

        // Safety net in case a reused video output didn't raise a fresh Vout event.
        if (!_hasVideo && _player.IsPlaying && time > 500 && _player.VideoTrackCount > 0)
        {
            _hasVideo = true;
            UpdateSurface();
        }
    }

    private void SeekPreview(double value)
    {
        TimeText.Text = MediaFormats.FormatTime((long)value);
        if (_player.IsSeekable && DateTime.UtcNow - _lastLiveSeek > TimeSpan.FromMilliseconds(120))
        {
            _player.Time = (long)value;
            _lastLiveSeek = DateTime.UtcNow;
        }
    }

    private void SeekCommit(double value)
    {
        if (_player.IsSeekable) _player.Time = (long)value;
        _isSeeking = false;
    }

    private void SeekBy(long deltaMs)
    {
        if (!_player.IsSeekable || _player.Length <= 0) return;
        var target = Math.Clamp(_player.Time + deltaMs, 0, Math.Max(0, _player.Length - 500));
        _player.Time = target;
        var sign = deltaMs >= 0 ? "+" : "−";
        ShowOsd($"{sign}{MediaFormats.FormatJump(Math.Abs(deltaMs) / 1000.0)}   {MediaFormats.FormatTime(target)} / {MediaFormats.FormatTime(_player.Length)}");
    }

    private void SeekToFraction(double fraction)
    {
        if (!_player.IsSeekable || _player.Length <= 0) return;
        var target = (long)(_player.Length * Math.Clamp(fraction, 0, 1));
        _player.Time = target;
        ShowOsd($"{MediaFormats.FormatTime(target)} / {MediaFormats.FormatTime(_player.Length)}");
    }

    private void ToggleAbLoop()
    {
        if (_player.Length <= 0) return;
        if (_abStart < 0)
        {
            _abStart = _player.Time;
            ShowOsd($"Loop start (A) at {MediaFormats.FormatTime(_abStart)}");
        }
        else if (_abEnd < 0 && _player.Time > _abStart + 200)
        {
            _abEnd = _player.Time;
            ShowOsd($"Looping {MediaFormats.FormatTime(_abStart)} → {MediaFormats.FormatTime(_abEnd)}");
        }
        else ClearAbLoop(silent: false);
        UpdateToggleButtons();
    }

    private void ClearAbLoop(bool silent)
    {
        var wasSet = _abStart >= 0;
        _abStart = _abEnd = -1;
        if (wasSet && !silent) ShowOsd("A-B loop off");
        UpdateToggleButtons();
    }

    private void UpdateAbMarkers()
    {
        var length = _player.Length;
        PlaceMarker(MarkerA, _abStart, length);
        PlaceMarker(MarkerB, _abEnd, length);
    }

    private void PlaceMarker(FrameworkElement marker, long time, long length)
    {
        if (time < 0 || length <= 0)
        {
            marker.Visibility = Visibility.Collapsed;
            return;
        }
        marker.Visibility = Visibility.Visible;
        System.Windows.Controls.Canvas.SetLeft(marker, SeekSlider.ActualWidth * time / length - 1);
    }

    // ===================== Volume & speed =====================

    private void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(Math.Round(VolumeSlider.Value) + delta, 0, 200);
        ShowOsd($"Volume {(int)VolumeSlider.Value}%");
    }

    private void ToggleMute()
    {
        _settings.Muted = !_settings.Muted;
        _player.Mute = _settings.Muted;
        UpdateMuteGlyph();
        ShowOsd(_settings.Muted ? "Muted" : $"Volume {(int)VolumeSlider.Value}%");
    }

    private void UpdateMuteGlyph()
    {
        var v = VolumeSlider.Value;
        MuteBtn.Content = _settings.Muted || v <= 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
    }

    private void SetRate(float rate)
    {
        _rate = (float)Math.Clamp(Math.Round(rate, 2), 0.25, 4.0);
        _player.SetRate(_rate);
        SpeedBtn.Content = $"{_rate.ToString("0.##", CultureInfo.InvariantCulture)}×";
        _suppressFx = true;
        if (_speedSlider != null) _speedSlider.Value = _rate;
        _suppressFx = false;
        ShowOsd($"Speed {_rate.ToString("0.##", CultureInfo.InvariantCulture)}×");
    }

    private static readonly double[] SpeedSteps = { 0.25, 0.5, 0.75, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4 };

    private void StepRate(int direction)
    {
        var next = direction > 0
            ? SpeedSteps.FirstOrDefault(s => s > _rate + 0.001, 4)
            : SpeedSteps.LastOrDefault(s => s < _rate - 0.001, 0.25);
        SetRate((float)next);
    }

    // ===================== Misc actions =====================

    private void TakeSnapshot()
    {
        if (_current == null || _player.VideoTrackCount <= 0)
        {
            ShowOsd("No video to capture");
            return;
        }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Lumen Player");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(_current.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var stamp = MediaFormats.FormatTime(_player.Time).Replace(':', '.');
        var file = Path.Combine(dir, $"{safe} [{stamp}] {DateTime.Now:yyyyMMdd-HHmmss}.png");
        ShowOsd(_player.TakeSnapshot(0, file, 0, 0) ? "Snapshot saved to Pictures\\Lumen Player" : "Snapshot failed");
    }

    private void LoadSubtitle(string path)
    {
        if (_current == null || !IsPlaybackActive(_player.State))
        {
            ShowOsd("Start playing a video before adding subtitles");
            return;
        }
        _player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
        ShowOsd($"Subtitles: {Path.GetFileName(path)}");
    }

    private void ToggleTopmost()
    {
        Topmost = !Topmost;
        _settings.AlwaysOnTop = Topmost;
        UpdateToggleButtons();
        ShowOsd(Topmost ? "Always on top" : "Always on top off");
    }

    private void UpdateToggleButtons()
    {
        var accent = (Brush)FindResource("Accent");
        var normal = (Brush)FindResource("TextPrimary");

        ShuffleBtn.Foreground = _settings.Shuffle ? accent : normal;
        RepeatBtn.Content = _settings.Repeat == RepeatMode.One ? "" : "";
        RepeatBtn.Foreground = _settings.Repeat == RepeatMode.Off ? normal : accent;
        AbBtn.Foreground = _abStart >= 0 ? accent : normal;
        AbBtn.Content = _abStart >= 0 && _abEnd < 0 ? "A-…" : "A-B";
        PinButton.Foreground = Topmost ? accent : normal;
        PinButton.Content = Topmost ? "" : "";
        PlaylistBtn.Foreground = Sidebar.Visibility == Visibility.Visible ? accent : normal;
        EffectsBtn.Foreground = EffectsPanel.Visibility == Visibility.Visible ? accent : normal;
    }

    // ===================== OSD & animation =====================

    private void ShowOsd(string text, double seconds = 1.6)
    {
        OsdText.Text = text;
        Osd.BeginAnimation(OpacityProperty, null);
        Osd.Opacity = 1;
        _osdTimer.Stop();
        _osdTimer.Interval = TimeSpan.FromSeconds(seconds);
        _osdTimer.Start();
    }

    private void Flash(string glyph)
    {
        FlashGlyph.Text = glyph;
        var duration = TimeSpan.FromMilliseconds(550);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        FlashIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(0.95, 0, duration) { EasingFunction = ease });
        var scale = (ScaleTransform)FlashIcon.RenderTransform;
        var grow = new DoubleAnimation(0.75, 1.35, duration) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private static void Fade(UIElement element, double to, int ms = 220)
    {
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase() });
    }

    private ImageSource GlyphImage(string glyph)
    {
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("IconFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            24, Brushes.White, 1.0);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawText(text, new Point((32 - text.Width) / 2, (32 - text.Height) / 2));
        var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // ===================== Power management =====================

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private static void KeepAwake(bool on)
    {
        const uint continuous = 0x80000000, system = 0x1, display = 0x2;
        SetThreadExecutionState(on ? continuous | system | display : continuous);
    }

    // ===================== Shutdown =====================

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveResumePosition();
        _settings.WindowMaximized = WindowState == WindowState.Maximized && !_isFullscreen;
        if (WindowState == WindowState.Normal && !_isFullscreen)
        {
            _settings.WindowWidth = ActualWidth;
            _settings.WindowHeight = ActualHeight;
        }
        _settings.Save();
        KeepAwake(false);

        // The process is exiting; stopping is enough and avoids racing the VideoView's own teardown.
        _uiTimer.Stop();
        _player.Stop();
    }
}
