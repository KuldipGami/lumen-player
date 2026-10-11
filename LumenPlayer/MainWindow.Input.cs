using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using LumenPlayer.Models;

namespace LumenPlayer;

// Window chrome, keyboard, mouse, drag & drop and fullscreen handling.
public partial class MainWindow
{
    private bool _isFullscreen;
    private WindowState _stateBeforeFullscreen;
    private bool _controlsVisible = true;
    private bool _menuOpen;
    private Point _lastMouse;
    private bool _overlayHooked;

    // ===================== Window chrome =====================

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Pin_Click(object sender, RoutedEventArgs e) => ToggleTopmost();

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        // A maximized chrome-less window overhangs the screen by the resize border; compensate.
        RootGrid.Margin = WindowState == WindowState.Maximized && !_isFullscreen ? new Thickness(7) : new Thickness(0);
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            _isFullscreen = true;
            TitleBar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            TopBar.Visibility = Visibility.Visible;
            TopBar.Opacity = _controlsVisible ? 1 : 0;
            FullscreenBtn.Content = "";
        }
        else
        {
            _isFullscreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            if (_stateBeforeFullscreen == WindowState.Maximized) WindowState = WindowState.Maximized;
            TitleBar.Visibility = Visibility.Visible;
            TopBar.Visibility = Visibility.Collapsed;
            FullscreenBtn.Content = "";
        }
        ApplyChrome();
        Window_StateChanged(this, EventArgs.Empty);
        ShowControls();
    }

    /// <summary>
    /// The custom title bar makes the top 38 px a window-drag area and the edges resize handles. In fullscreen
    /// there's no title bar, and those areas would swallow clicks meant for the playlist header and edge buttons.
    /// </summary>
    private void ApplyChrome()
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = _isFullscreen ? 0 : 38,
            ResizeBorderThickness = _isFullscreen ? new Thickness(0) : new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
    }

    // ===================== Overlay (video surface) =====================

    private void OverlayRoot_Loaded(object sender, RoutedEventArgs e)
    {
        // The overlay lives in its own transparent window above the native video surface;
        // keyboard input that lands there must reach the same handler.
        var host = GetWindow(OverlayRoot);
        if (host != null && host != this && !_overlayHooked)
        {
            host.PreviewKeyDown += OnPreviewKeyDown;
            _overlayHooked = true;
        }
    }

    private void OverlayRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        static Visibility Show(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

        NowPlayingText.Visibility = Show(w >= 1080);
        AbBtn.Visibility = Show(w >= 960);
        ShuffleBtn.Visibility = RepeatBtn.Visibility = Show(w >= 900);
        SnapshotBtn.Visibility = StopBtn.Visibility = Show(w >= 800);
        VolumeText.Visibility = Show(w >= 740);
        VolumeSlider.Visibility = Show(w >= 680);
        RewindBtn.Visibility = ForwardBtn.Visibility = Show(w >= 600);
        EffectsPanel.Width = Math.Min(420, Math.Max(280, w - 36));
        WelcomePanel.Width = Math.Min(520, Math.Max(260, w - 40));
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(OverlayRoot);
        if (Math.Abs(p.X - _lastMouse.X) < 2 && Math.Abs(p.Y - _lastMouse.Y) < 2) return;
        _lastMouse = p;
        ShowControls();
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource, ControlsPanel) || IsWithin(e.OriginalSource, EffectsPanel)) return;
        Keyboard.Focus(OverlayRoot);

        if (EffectsPanel.Visibility == Visibility.Visible)
        {
            ToggleEffects();
            return;
        }
        if (e.ClickCount == 2) ToggleFullscreen();
    }

    private void Overlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource, EffectsPanel)) return;
        ShowMenu(BuildMainMenu(), null);
        e.Handled = true;
    }

    private void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (IsWithin(e.OriginalSource, EffectsPanel)) return;
        var up = e.Delta > 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            SeekBy(up ? Ms(_settings.ShortJump) : -Ms(_settings.ShortJump));
        else
            ChangeVolume(up ? _settings.VolumeStep : -_settings.VolumeStep);
        e.Handled = true;
    }

    private void ShowControls()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
        if (_controlsVisible) return;

        _controlsVisible = true;
        ControlsPanel.IsHitTestVisible = true;
        Fade(ControlsPanel, 1);
        if (_isFullscreen) Fade(TopBar, 1);
        OverlayRoot.ClearValue(CursorProperty);
    }

    private void AutoHideControls()
    {
        if (!_player.IsPlaying || _isSeeking || _menuOpen || ControlsPanel.IsMouseOver
            || EffectsPanel.Visibility == Visibility.Visible)
            return; // timer keeps running; we'll re-check shortly

        _hideTimer.Stop();
        if (!_controlsVisible) return;
        _controlsVisible = false;
        ControlsPanel.IsHitTestVisible = false;
        Fade(ControlsPanel, 0, 400);
        Fade(TopBar, 0, 400);
        if (OverlayRoot.IsMouseOver) OverlayRoot.Cursor = Cursors.None;
    }

    private static bool IsWithin(object? source, DependencyObject container)
    {
        var node = source as DependencyObject;
        while (node != null)
        {
            if (node == container) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>
    /// Makes a slider respond like a modern scrubber: press anywhere on the track and drag.
    /// Double-click resets it to the default stored in <see cref="FrameworkElement.Tag"/>.
    /// </summary>
    private static void MakeScrubbable(Slider slider, Action? onStart = null, Action<double>? onDrag = null, Action<double>? onRelease = null)
    {
        double ValueAt(Point p)
        {
            var ratio = slider.Orientation == Orientation.Horizontal
                ? p.X / Math.Max(1, slider.ActualWidth)
                : 1 - p.Y / Math.Max(1, slider.ActualHeight);
            return slider.Minimum + Math.Clamp(ratio, 0, 1) * (slider.Maximum - slider.Minimum);
        }

        slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2 && slider.Tag is double reset)
            {
                slider.Value = reset;
                return;
            }
            onStart?.Invoke();
            slider.CaptureMouse();
            slider.Value = ValueAt(e.GetPosition(slider));
            onDrag?.Invoke(slider.Value);
        };
        slider.PreviewMouseMove += (_, e) =>
        {
            if (!slider.IsMouseCaptured) return;
            slider.Value = ValueAt(e.GetPosition(slider));
            onDrag?.Invoke(slider.Value);
        };
        slider.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!slider.IsMouseCaptured) return;
            slider.ReleaseMouseCapture();
            onRelease?.Invoke(slider.Value);
            e.Handled = true;
        };
    }

    private void Seek_MouseMove(object sender, MouseEventArgs e)
    {
        var length = _player.Length;
        if (length <= 0)
        {
            SeekTip.Visibility = Visibility.Collapsed;
            return;
        }
        var x = Math.Clamp(e.GetPosition(SeekSlider).X, 0, SeekSlider.ActualWidth);
        SeekTipText.Text = MediaFormats.FormatTime((long)(length * x / Math.Max(1, SeekSlider.ActualWidth)));
        SeekTip.Visibility = Visibility.Visible;
        SeekTip.UpdateLayout();
        Canvas.SetLeft(SeekTip, Math.Clamp(x - SeekTip.ActualWidth / 2, -20, SeekSlider.ActualWidth - SeekTip.ActualWidth + 20));
    }

    private void Seek_MouseLeave(object sender, MouseEventArgs e) => SeekTip.Visibility = Visibility.Collapsed;

    // ===================== Keyboard =====================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);

        // Let the playlist keep its own navigation keys while it has focus.
        if (PlaylistBox.IsKeyboardFocusWithin && !ctrl &&
            key is Key.Up or Key.Down or Key.Enter or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            return;

        if (HandleKey(key, ctrl, shift, alt))
        {
            e.Handled = true;
            ShowControlsIfKeyboardSeek(key);
        }
    }

    private void ShowControlsIfKeyboardSeek(Key key)
    {
        if (key is Key.Left or Key.Right or (>= Key.D0 and <= Key.D9) or (>= Key.NumPad0 and <= Key.NumPad9) or Key.Home)
            ShowControls();
    }

    private bool HandleKey(Key key, bool ctrl, bool shift, bool alt)
    {
        if (alt)
        {
            switch (key)
            {
                case Key.Enter: ToggleFullscreen(); return true;
                case Key.Left: SeekBy(-Ms(_settings.LongJump)); return true;
                case Key.Right: SeekBy(Ms(_settings.LongJump)); return true;
                default: return false; // keep Alt+F4 etc.
            }
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.O: OpenFiles(); return true;
                case Key.F: OpenFolder(); return true;
                case Key.N: case Key.U: OpenUrl(); return true;
                case Key.L: TogglePlaylist(); return true;
                case Key.E: ToggleEffects(); return true;
                case Key.I: ShowMediaInfo(); return true;
                case Key.T: ToggleTopmost(); return true;
                case Key.S: SavePlaylist(); return true;
                case Key.Q: case Key.W: Close(); return true;
                case Key.Left: SeekBy(-Ms(_settings.Jump)); return true;
                case Key.Right: SeekBy(Ms(_settings.Jump)); return true;
                case Key.Up: ChangeVolume(2 * _settings.VolumeStep); return true;
                case Key.Down: ChangeVolume(-2 * _settings.VolumeStep); return true;
                case Key.P: ShowSettings(); return true;
                default: return false;
            }
        }

        switch (key)
        {
            case Key.Space: case Key.MediaPlayPause: case Key.Play: TogglePlay(); break;
            case Key.F: case Key.F11: ToggleFullscreen(); break;
            case Key.Escape:
                if (EffectsPanel.Visibility == Visibility.Visible) ToggleEffects();
                else if (_isFullscreen) ToggleFullscreen();
                else return false;
                break;
            case Key.Left: SeekBy(-Ms(shift ? _settings.VeryShortJump : _settings.ShortJump)); break;
            case Key.Right: SeekBy(Ms(shift ? _settings.VeryShortJump : _settings.ShortJump)); break;
            case Key.Up: ChangeVolume(_settings.VolumeStep); break;
            case Key.Down: ChangeVolume(-_settings.VolumeStep); break;
            case Key.M: ToggleMute(); break;
            case Key.N: case Key.PageDown: case Key.MediaNextTrack: PlayNext(); break;
            case Key.P: case Key.PageUp: case Key.MediaPreviousTrack: PlayPrevious(); break;
            case Key.S when shift: TakeSnapshot(); break;
            case Key.S: case Key.MediaStop: StopPlayback(); break;
            case Key.OemOpenBrackets: case Key.OemMinus: case Key.Subtract: StepRate(-1); break;
            case Key.OemCloseBrackets: case Key.OemPlus: case Key.Add: StepRate(+1); break;
            case Key.Back: SetRate(1f); break;
            case Key.E: _player.NextFrame(); UpdatePlayState(); break;
            case Key.V: CycleSubtitle(); break;
            case Key.B: CycleAudioTrack(); break;
            case Key.A: CycleAspect(); break;
            case Key.C: CycleCrop(); break;
            case Key.Z: CycleZoom(); break;
            case Key.G: NudgeSubtitleDelay(-50); break;
            case Key.H: NudgeSubtitleDelay(50); break;
            case Key.J: NudgeAudioDelay(-50); break;
            case Key.K: NudgeAudioDelay(50); break;
            case Key.L when shift: ToggleAbLoop(); break;
            case Key.L: CycleRepeat(); break;
            case Key.R: ToggleShuffle(); break;
            case Key.T:
                ShowOsd(_player.Length > 0
                    ? $"{MediaFormats.FormatTime(_player.Time)} / {MediaFormats.FormatTime(_player.Length)}"
                    : DateTime.Now.ToString("t"));
                break;
            case Key.I: ShowMediaInfo(); break;
            case Key.Home: SeekToFraction(0); break;
            case Key.F1: ShowShortcuts(); break;
            case >= Key.D0 and <= Key.D9: SeekToFraction((key - Key.D0) / 10.0); break;
            case >= Key.NumPad0 and <= Key.NumPad9: SeekToFraction((key - Key.NumPad0) / 10.0); break;
            default: return false;
        }
        return true;
    }

    // ===================== Drag & drop =====================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        // Dropping onto the playlist queues; dropping onto the video plays immediately.
        var play = !IsWithin(e.OriginalSource, Sidebar);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            AddPaths(files, play);
        else if (e.Data.GetData(DataFormats.Text) is string text)
            AddPaths(text.Split('\n', StringSplitOptions.RemoveEmptyEntries), play);
        e.Handled = true;
    }

    // ===================== Transport buttons =====================

    private void PlayPause_Click(object sender, EventArgs e) => TogglePlay();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void Prev_Click(object sender, EventArgs e) => PlayPrevious();
    private void Next_Click(object sender, EventArgs e) => PlayNext();
    private void Rewind_Click(object sender, RoutedEventArgs e) => SeekBy(-Ms(_settings.Jump));
    private void Forward_Click(object sender, RoutedEventArgs e) => SeekBy(Ms(_settings.Jump));
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void Snapshot_Click(object sender, RoutedEventArgs e) => TakeSnapshot();
    private void AbLoop_Click(object sender, RoutedEventArgs e) => ToggleAbLoop();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e) => CycleRepeat();
    private void Effects_Click(object sender, RoutedEventArgs e) => ToggleEffects();
    private void CloseEffects_Click(object sender, RoutedEventArgs e) => ToggleEffects();
    private void TogglePlaylist_Click(object sender, RoutedEventArgs e) => TogglePlaylist();
    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFiles();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder();
    private void OpenUrl_Click(object sender, RoutedEventArgs e) => OpenUrl();

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) OpenRecent(path);
    }

    private void ClearRecent_Click(object sender, MouseButtonEventArgs e)
    {
        _settings.RecentFiles.Clear();
        RefreshRecent();
    }

    private void LengthText_Click(object sender, MouseButtonEventArgs e)
    {
        _showRemaining = !_showRemaining;
        UpdateTimeline();
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        var v = (int)Math.Round(e.NewValue);
        _player.Volume = v;
        _settings.Volume = v;
        VolumeText.Text = $"{v}%";
        if (_settings.Muted && v > 0 && e.OldValue < e.NewValue)
        {
            _settings.Muted = false;
            _player.Mute = false;
        }
        UpdateMuteGlyph();
    }

    private void Speed_Click(object sender, RoutedEventArgs e) => ShowMenu(BuildSpeedMenu(), (UIElement)sender);
    private void AudioMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(BuildAudioMenu(), (UIElement)sender);
    private void SubtitleMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(BuildSubtitleMenu(), (UIElement)sender);
    private void VideoMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(BuildVideoMenu(), (UIElement)sender);
    private void Menu_Click(object sender, RoutedEventArgs e) => ShowMenu(BuildMainMenu(), (UIElement)sender, PlacementMode.Bottom);

    private void ToggleShuffle()
    {
        _settings.Shuffle = !_settings.Shuffle;
        UpdateToggleButtons();
        ShowOsd(_settings.Shuffle ? "Shuffle on" : "Shuffle off");
    }

    private void CycleRepeat()
    {
        _settings.Repeat = _settings.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        UpdateToggleButtons();
        ShowOsd(_settings.Repeat switch
        {
            RepeatMode.All => "Repeat all",
            RepeatMode.One => "Repeat one",
            _ => "Repeat off",
        });
    }

    private void TogglePlaylist()
    {
        var show = Sidebar.Visibility != Visibility.Visible;
        Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _settings.ShowPlaylist = show;
        UpdateToggleButtons();
    }

    // ===================== Playlist panel =====================

    private void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource, PlaylistBox) && PlaylistBox.SelectedItem is PlaylistItem item) PlayItem(item);
    }

    private void PlaylistBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PlaylistBox.SelectedItem is PlaylistItem item)
        {
            PlayItem(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void RemoveSelected()
    {
        foreach (var item in PlaylistBox.SelectedItems.Cast<PlaylistItem>().ToList()) _playlist.Remove(item);
    }

    private void MoveSelected(int direction)
    {
        if (PlaylistBox.SelectedItem is not PlaylistItem item) return;
        var from = _playlist.IndexOf(item);
        var to = from + direction;
        if (to < 0 || to >= _playlist.Count) return;
        _playlist.Move(from, to);
        PlaylistBox.SelectedItem = item;
    }

    private static void RevealInExplorer(string path)
    {
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private void PlaylistAdd_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Add files…", () => OpenFiles(play: false), ""));
        menu.Items.Add(Item("Add folder…", () => OpenFolder(play: false), ""));
        menu.Items.Add(Item("Add link (YouTube, stream)…", () => OpenUrl(play: false), ""));
        ShowMenu(menu, (UIElement)sender, PlacementMode.Bottom);
    }

    private void SavePlaylist_Click(object sender, RoutedEventArgs e) => SavePlaylist();

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e) => _playlist.Clear();

    private void SavePlaylist()
    {
        if (_playlist.Count == 0)
        {
            ShowOsd("Playlist is empty");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save playlist",
            Filter = "M3U playlist|*.m3u8",
            FileName = "Playlist.m3u8",
        };
        if (dlg.ShowDialog(this) != true) return;
        var lines = new List<string> { "#EXTM3U" };
        foreach (var item in _playlist)
        {
            lines.Add($"#EXTINF:-1,{item.Title}");
            lines.Add(item.Path);
        }
        File.WriteAllLines(dlg.FileName, lines, new System.Text.UTF8Encoding(false));
        ShowOsd("Playlist saved");
    }

    private void PlaylistContext_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        var selected = PlaylistBox.SelectedItem as PlaylistItem;
        menu.Items.Add(Item("Play", () => { if (selected != null) PlayItem(selected); }, "", "Enter", enabled: selected != null));
        menu.Items.Add(Item("Remove", RemoveSelected, "", "Del", enabled: selected != null));
        menu.Items.Add(Item("Move up", () => MoveSelected(-1), "", enabled: selected != null));
        menu.Items.Add(Item("Move down", () => MoveSelected(1), "", enabled: selected != null));
        menu.Items.Add(Item("Show in Explorer", () => RevealInExplorer(selected!.Path), "", enabled: selected is { IsUrl: false }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Sort by name", SortPlaylist, "", enabled: _playlist.Count > 1));
        menu.Items.Add(Item("Add files…", () => OpenFiles(play: false), ""));
        menu.Items.Add(Item("Add folder…", () => OpenFolder(play: false), ""));
        menu.Items.Add(Item("Clear playlist", () => _playlist.Clear(), "", enabled: _playlist.Count > 0));
    }

    private void SortPlaylist()
    {
        var sorted = _playlist.OrderBy(i => i.Title, MediaFormats.NaturalOrder).ToList();
        _playlist.Clear();
        foreach (var item in sorted) _playlist.Add(item);
    }
}
