using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LumenPlayer;

/// <summary>Themed modal used for prompts (Open URL) and read-only sheets (media info, shortcuts).</summary>
public partial class DialogWindow : Window
{
    private DialogWindow(Window? owner, string title)
    {
        InitializeComponent();
        Owner = owner;
        if (owner == null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TitleText.Text = title;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    public static string? Prompt(Window owner, string title, string message, string initial = "", string okText = "Open")
    {
        var dlg = new DialogWindow(owner, title);
        var box = new TextBox { Style = (Style)dlg.FindResource("InputBox"), Text = initial, Width = 480 };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { dlg.DialogResult = true; e.Handled = true; }
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = (System.Windows.Media.Brush)dlg.FindResource("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        dlg.BodyHost.Content = panel;
        dlg.OkButton.Content = okText;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    public static void ShowSheet(Window owner, string title, UIElement body)
    {
        var dlg = new DialogWindow(owner, title);
        dlg.BodyHost.Content = body;
        dlg.CancelButton.Visibility = Visibility.Collapsed;
        dlg.OkButton.Content = "Close";
        dlg.ShowDialog();
    }

    /// <summary>Modal form with Save/Cancel. <paramref name="validate"/> can keep the dialog open.</summary>
    public static bool ShowForm(Window owner, string title, UIElement body, Func<bool> validate, string okText = "Save")
    {
        var dlg = new DialogWindow(owner, title) { _validate = validate };
        dlg.BodyHost.Content = body;
        dlg.OkButton.Content = okText;
        return dlg.ShowDialog() == true;
    }

    private Func<bool>? _validate;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_validate == null || _validate()) DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
