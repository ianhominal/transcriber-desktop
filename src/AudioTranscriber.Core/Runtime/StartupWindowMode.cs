namespace AudioTranscriber.Core.Runtime;

/// <summary>
/// Pure decision of whether the main window should start minimized to the tray or visible,
/// based on the process' launch arguments. Kept separate from App.xaml.cs (which owns the
/// actual Show()/Hide() calls) so it is testable without a GUI. The only trigger today is
/// <see cref="AutoStartRegistration.MinimizedArg"/>, the flag AutoStartRegistration writes into
/// HKCU\...\Run so a launch coming from Windows auto-start starts minimized instead of as a
/// normal window (see changelog 2026-07-09).
/// </summary>
public static class StartupWindowMode
{
    public static bool ShouldStartMinimized(string[] args) =>
        Array.Exists(args, arg => arg == AutoStartRegistration.MinimizedArg);
}
