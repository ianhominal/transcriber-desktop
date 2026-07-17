using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;

namespace AudioTranscriber.App.Controls;

/// <summary>
/// Code-behind for the custom title bar (see TitleBar.xaml for the WindowChrome contract).
/// Minimize/maximize/restore go through <see cref="SystemCommands"/> so they stay real window
/// operations (Aero snap, taskbar preview, etc. keep working). Close calls
/// <see cref="Window.Close"/> directly — NOT <see cref="SystemCommands.CloseWindow"/> — so
/// MainWindow's existing minimize-to-tray Closing handler (see App.xaml.cs
/// OnMainWindowClosing) still runs unmodified; SystemCommands.CloseWindow ends up calling the
/// same Close() internally, but going through Window.Close() directly keeps the intent explicit.
/// </summary>
public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty TitleTextProperty = DependencyProperty.Register(
        nameof(TitleText), typeof(string), typeof(TitleBar), new PropertyMetadata("Audio Transcriber"));

    public static readonly DependencyProperty IconSourceProperty = DependencyProperty.Register(
        nameof(IconSource), typeof(ImageSource), typeof(TitleBar), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowMaximizeRestoreProperty = DependencyProperty.Register(
        nameof(ShowMaximizeRestore), typeof(bool), typeof(TitleBar), new PropertyMetadata(true));

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    public ImageSource? IconSource
    {
        get => (ImageSource?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    /// <summary>False for fixed-size dialogs (SettingsWindow/SyncWindow, ResizeMode="NoResize"):
    /// hides the maximize/restore button since there is nothing to maximize into.</summary>
    public bool ShowMaximizeRestore
    {
        get => (bool)GetValue(ShowMaximizeRestoreProperty);
        set => SetValue(ShowMaximizeRestoreProperty, value);
    }

    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        IconSource = AppIconLoader.Load();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null)
            return;

        _window.StateChanged += OnWindowStateChanged;
        UpdateMaximizeGlyph();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
            _window.StateChanged -= OnWindowStateChanged;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateMaximizeGlyph();

    private void UpdateMaximizeGlyph()
    {
        if (_window is null)
            return;

        // Same glyph pair Windows itself uses natively for this button: a single square to
        // maximize, two overlapping squares to restore.
        var isMaximized = _window.WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = isMaximized ? "❒" : "☐";
        AutomationProperties.SetName(MaximizeRestoreButton, isMaximized ? "Restaurar" : "Maximizar");
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
            SystemCommands.MinimizeWindow(window);
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window)
            return;

        if (window.WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(window);
        else
            SystemCommands.MaximizeWindow(window);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        Window.GetWindow(this)?.Close();
}
