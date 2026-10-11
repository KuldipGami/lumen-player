using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibVLCSharp.Shared;

namespace LumenPlayer;

// Image adjustments, the 10-band equalizer and A/V sync controls.
public partial class MainWindow
{
    private readonly List<(Slider Slider, VideoAdjustOption Option)> _adjustSliders = new();
    private readonly List<Slider> _eqSliders = new();
    private Equalizer _equalizer = new();
    private Slider? _preampSlider, _audioDelaySlider, _subDelaySlider, _speedSlider;
    private bool _suppressFx;

    private void BuildEffectsPanel()
    {
        // --- Video adjustments ---
        AddAdjustRow("Brightness", VideoAdjustOption.Brightness, 0, 2, 1);
        AddAdjustRow("Contrast", VideoAdjustOption.Contrast, 0, 2, 1);
        AddAdjustRow("Saturation", VideoAdjustOption.Saturation, 0, 3, 1);
        AddAdjustRow("Hue", VideoAdjustOption.Hue, -180, 180, 0, v => $"{v:0}°");
        AddAdjustRow("Gamma", VideoAdjustOption.Gamma, 0.1, 3, 1);

        // --- Sync & speed ---
        _audioDelaySlider = AddSliderRow(SyncRows, "Audio delay", -5000, 5000, 0, v => $"{v:+0;−0;0} ms",
            v => _player.SetAudioDelay((long)(v * 1000)), step: 10);
        _subDelaySlider = AddSliderRow(SyncRows, "Subtitle delay", -10000, 10000, 0, v => $"{v:+0;−0;0} ms",
            v => _player.SetSpuDelay((long)(v * 1000)), step: 10);
        _speedSlider = AddSliderRow(SyncRows, "Speed", 0.25, 4, 1, v => $"{v:0.00}×",
            v => SetRate((float)v), step: 0.05);

        // --- Equalizer ---
        BuildEqualizer();
    }

    private void AddAdjustRow(string label, VideoAdjustOption option, double min, double max, double def, Func<double, string>? format = null)
    {
        var slider = AddSliderRow(VideoRows, label, min, max, def, format ?? (v => v.ToString("0.00")), v =>
        {
            if (AdjustEnable.IsChecked != true) AdjustEnable.IsChecked = true; // touching a slider turns adjustments on
            else _player.SetAdjustFloat(option, (float)v);
        });
        _adjustSliders.Add((slider, option));
    }

    private Slider AddSliderRow(Panel parent, string label, double min, double max, double value,
        Func<double, string> format, Action<double> apply, double step = 0)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        var name = new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextSecondary"),
        };
        var slider = new Slider
        {
            Style = (Style)FindResource("ModernSlider"),
            Minimum = min,
            Maximum = max,
            Value = value,
            Tag = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var readout = new TextBlock
        {
            Text = format(value),
            Style = (Style)FindResource("TimeLabel"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(readout);
        parent.Children.Add(grid);

        slider.ValueChanged += (_, _) =>
        {
            if (step > 0)
            {
                var snapped = Math.Round(slider.Value / step) * step;
                if (Math.Abs(snapped - slider.Value) > 1e-9)
                {
                    slider.Value = snapped;
                    return;
                }
            }
            readout.Text = format(slider.Value);
            if (!_suppressFx) apply(slider.Value);
        };
        MakeScrubbable(slider);
        return slider;
    }

    private void ApplyVideoAdjust()
    {
        var enabled = AdjustEnable.IsChecked == true;
        _player.SetAdjustInt(VideoAdjustOption.Enable, enabled ? 1 : 0);
        if (!enabled) return;
        foreach (var (slider, option) in _adjustSliders) _player.SetAdjustFloat(option, (float)slider.Value);
    }

    private void AdjustEnable_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ApplyVideoAdjust();
    }

    private void ResetAdjust_Click(object sender, RoutedEventArgs e)
    {
        _suppressFx = true;
        foreach (var (slider, _) in _adjustSliders) slider.Value = (double)slider.Tag;
        _suppressFx = false;
        AdjustEnable.IsChecked = false;
        ApplyVideoAdjust();
    }

    private void ResetSyncSliders()
    {
        // A/V delays are per-file in LibVLC; reset the UI to match a fresh file.
        _suppressFx = true;
        if (_audioDelaySlider != null) _audioDelaySlider.Value = 0;
        if (_subDelaySlider != null) _subDelaySlider.Value = 0;
        _suppressFx = false;
    }

    // ===================== Equalizer =====================

    private void BuildEqualizer()
    {
        var bands = (int)_equalizer.BandCount;
        var saved = _settings.EqualizerBands;
        if (saved.Length == bands)
        {
            for (var i = 0; i < bands; i++) _equalizer.SetAmp(saved[i], (uint)i);
            _equalizer.SetPreamp(_settings.Preamp);
        }

        for (var i = 0; i <= bands; i++) EqBands.ColumnDefinitions.Add(new ColumnDefinition());

        _preampSlider = AddEqColumn(0, "Pre", _equalizer.Preamp, v =>
        {
            _equalizer.SetPreamp((float)v);
            _settings.Preamp = (float)v;
        });

        for (var i = 0; i < bands; i++)
        {
            var band = (uint)i;
            var freq = _equalizer.BandFrequency(band);
            var label = freq >= 1000 ? $"{freq / 1000:0.#}k" : $"{freq:0}";
            _eqSliders.Add(AddEqColumn(i + 1, label, _equalizer.Amp(band), v => _equalizer.SetAmp((float)v, band)));
        }

        EqPresetText.Text = _settings.EqualizerPreset;
        _suppressFx = true;
        EqEnable.IsChecked = _settings.EqualizerEnabled;
        _suppressFx = false;
    }

    private Slider AddEqColumn(int column, string label, double value, Action<double> apply)
    {
        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition());
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var readout = new TextBlock
        {
            FontSize = 10.5,
            Foreground = (Brush)FindResource("TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var slider = new Slider
        {
            Style = (Style)FindResource("VerticalSlider"),
            Minimum = -20,
            Maximum = 20,
            Value = value,
            Tag = 0.0,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var caption = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = column == 0 ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetRow(slider, 1);
        Grid.SetRow(caption, 2);
        stack.Children.Add(readout);
        stack.Children.Add(slider);
        stack.Children.Add(caption);
        Grid.SetColumn(stack, column);
        EqBands.Children.Add(stack);

        readout.Text = $"{value:+0.0;−0.0;0}";
        slider.ValueChanged += (_, _) =>
        {
            readout.Text = $"{slider.Value:+0.0;−0.0;0}";
            apply(slider.Value);
            if (_suppressFx) return;
            EqPresetText.Text = _settings.EqualizerPreset = "Custom";
            SaveEqBands();
            if (EqEnable.IsChecked != true) EqEnable.IsChecked = true;
            else ApplyEqualizer();
        };
        MakeScrubbable(slider);
        return slider;
    }

    private void SaveEqBands()
    {
        _settings.EqualizerBands = Enumerable.Range(0, (int)_equalizer.BandCount).Select(i => _equalizer.Amp((uint)i)).ToArray();
        _settings.Preamp = _equalizer.Preamp;
    }

    private void ApplyEqualizer()
    {
        if (EqEnable.IsChecked == true) _player.SetEqualizer(_equalizer);
        else _player.UnsetEqualizer();
    }

    private void EqEnable_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _suppressFx) return;
        _settings.EqualizerEnabled = EqEnable.IsChecked == true;
        ApplyEqualizer();
    }

    private void EqPreset_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        for (uint i = 0; i < _equalizer.PresetCount; i++)
        {
            var index = i;
            var name = _equalizer.PresetName(i) ?? $"Preset {i + 1}";
            menu.Items.Add(Item(name, () => LoadEqPreset(index), isChecked: name == _settings.EqualizerPreset));
        }
        ShowMenu(menu, (UIElement)sender, System.Windows.Controls.Primitives.PlacementMode.Bottom);
    }

    private void LoadEqPreset(uint index)
    {
        _equalizer = new Equalizer(index);
        _suppressFx = true;
        _preampSlider!.Value = _equalizer.Preamp;
        for (var i = 0; i < _eqSliders.Count; i++) _eqSliders[i].Value = _equalizer.Amp((uint)i);
        _suppressFx = false;

        EqPresetText.Text = _settings.EqualizerPreset = _equalizer.PresetName(index) ?? $"Preset {index + 1}";
        SaveEqBands();
        if (EqEnable.IsChecked != true) EqEnable.IsChecked = true;
        else ApplyEqualizer();
    }

    // ===================== Panel =====================

    private void ToggleEffects()
    {
        var show = EffectsPanel.Visibility != Visibility.Visible;
        EffectsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateToggleButtons();
        if (show) ShowControls();
    }

    private void FxTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        VideoTab.Visibility = TabVideo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EqTab.Visibility = TabEq.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SyncTab.Visibility = TabSync.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
}
