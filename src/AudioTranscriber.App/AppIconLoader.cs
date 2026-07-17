using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioTranscriber.App;

/// <summary>
/// Loads the app icon as a WPF <see cref="ImageSource"/> for the custom title bar (see
/// Controls/TitleBar.xaml), reusing the same embedded-resource-stream approach as
/// <see cref="TrayIconService.LoadTrayIcon"/> — deliberately NOT a pack URI
/// (pack://application:,,,/appicon.ico): pack URIs depend on Assembly.Location, which is empty in
/// this app's PublishSingleFile=true build (see TrayIconService.LoadTrayIcon's XML doc for the
/// full story and the crash it caused). Reading the .ico via Assembly.GetManifestResourceStream
/// works identically in single-file and normal builds.
/// <para/>
/// Best-effort and cached: a missing/corrupt icon must never block a window from opening, so any
/// failure just returns null (the title bar simply shows no icon image) and the result is cached
/// after the first successful/failed attempt since every window asks for the same bitmap.
/// </summary>
public static class AppIconLoader
{
    private static ImageSource? _cached;
    private static bool _attempted;

    public static ImageSource? Load()
    {
        if (_attempted)
            return _cached;
        _attempted = true;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("AppIcon.ico");
            if (stream is null)
                return null;

            using var icon = new Icon(stream);
            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // Freeze() makes it immutable/thread-safe so it's safe to cache and hand out to every
            // window's TitleBar instance.
            bitmapSource.Freeze();
            _cached = bitmapSource;
            return _cached;
        }
        catch
        {
            return null;
        }
    }
}
