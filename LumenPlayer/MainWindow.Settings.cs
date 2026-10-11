using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LumenPlayer.Models;

namespace LumenPlayer;

// Preferences dialog: seek distances, volume step and YouTube support updates.
public partial class MainWindow
{
    private static long Ms(double seconds) => (long)Math.Round(seconds * 1000);

    /// <summary>Puts the configured jump lengths into tooltips so they always match what the keys do.</summary>
    private void ApplyJumpLabels()
    {
        var jump = MediaFormats.FormatJump(_settings.Jump);
        RewindBtn.ToolTip = $"Back {jump} (Ctrl+←)";
        ForwardBtn.ToolTip = $"Forward {jump} (Ctrl+→)";
    }

    private void ShowSettings()
    {
        var panel = new StackPanel { Width = 500 };
        var errors = new List<Func<bool>>();

        TextBlock Section(string title) => new()
        {
            Text = title.ToUpperInvariant(),
            Style = (Style)FindResource("SectionLabel"),
            Foreground = (Brush)FindResource("Accent"),
            Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 20, 0, 8),
        };

        // A labelled number box. Returns a getter for the validated value (null while invalid).
        Func<double?> NumberRow(string label, string hint, double value, string unit, double min, double max)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = label, FontSize = 13.5 });
            text.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = (Brush)FindResource("TextSecondary") });

            var box = new TextBox
            {
                Style = (Style)FindResource("InputBox"),
                Text = value.ToString("0.##", CultureInfo.CurrentCulture),
                Padding = new Thickness(8, 6, 8, 6),
                TextAlignment = TextAlignment.Right,
            };
            var unitText = new TextBlock
            {
                Text = unit,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondary"),
            };
            Grid.SetColumn(box, 1);
            Grid.SetColumn(unitText, 2);
            grid.Children.Add(text);
            grid.Children.Add(box);
            grid.Children.Add(unitText);
            panel.Children.Add(grid);

            double? Parse()
            {
                var ok = double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v)
                         || double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
                return ok && v >= min && v <= max ? v : null;
            }

            box.TextChanged += (_, _) => box.ClearValue(Control.BorderBrushProperty);
            errors.Add(() =>
            {
                if (Parse() != null) return true;
                box.BorderBrush = Brushes.IndianRed;
                box.ToolTip = $"Enter a number from {min.ToString(CultureInfo.CurrentCulture)} to {max.ToString(CultureInfo.CurrentCulture)}";
                return false;
            });
            return Parse;
        }

        panel.Children.Add(Section("Seeking"));
        var veryShort = NumberRow("Very short jump", "Shift + ← / →", _settings.VeryShortJump, "seconds", 0.1, 3600);
        var shortJump = NumberRow("Short jump", "← / →  and  Ctrl/Shift + mouse wheel", _settings.ShortJump, "seconds", 0.1, 3600);
        var jump = NumberRow("Jump", "Ctrl + ← / →  and the back / forward buttons", _settings.Jump, "seconds", 0.1, 3600);
        var longJump = NumberRow("Long jump", "Alt + ← / →", _settings.LongJump, "seconds", 0.1, 36000);

        panel.Children.Add(Section("Volume"));
        var volumeStep = NumberRow("Volume step", "↑ / ↓  and mouse wheel", _settings.VolumeStep, "%", 1, 50);

        panel.Children.Add(Section("YouTube & online video"));
        panel.Children.Add(BuildYouTubeRow());

        var saved = DialogWindow.ShowForm(this, "Settings", panel, () => errors.Select(check => check()).ToList().All(ok => ok));
        if (!saved) return;

        _settings.VeryShortJump = veryShort()!.Value;
        _settings.ShortJump = shortJump()!.Value;
        _settings.Jump = jump()!.Value;
        _settings.LongJump = longJump()!.Value;
        _settings.VolumeStep = (int)Math.Round(volumeStep()!.Value);
        _settings.Save();
        ApplyJumpLabels();
        ShowOsd("Settings saved");
    }

    private UIElement BuildYouTubeRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = "yt-dlp", FontSize = 13.5 });
        var status = new TextBlock
        {
            Text = StreamResolver.IsAvailable ? "Plays links from YouTube, Vimeo, Twitch and 1,800+ sites" : "Not installed",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        };
        info.Children.Add(status);

        var update = new Button
        {
            Style = (Style)FindResource("SecondaryButton"),
            Content = "Check for updates",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(12, 0, 0, 0),
            IsEnabled = StreamResolver.IsAvailable,
            VerticalAlignment = VerticalAlignment.Center,
        };
        update.Click += async (_, _) =>
        {
            update.IsEnabled = false;
            status.Text = "Checking…";
            try
            {
                status.Text = await StreamResolver.UpdateAsync();
            }
            catch (Exception ex)
            {
                status.Text = $"Update failed: {ex.Message}";
            }
            update.IsEnabled = true;
        };
        Grid.SetColumn(update, 1);
        grid.Children.Add(info);
        grid.Children.Add(update);
        return grid;
    }
}
