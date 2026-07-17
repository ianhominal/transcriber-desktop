namespace AudioTranscriber.Core.Runtime;

/// <summary>
/// Lógica pura (sin dependencia de Microsoft.Win32.Registry) del registro de inicio automático
/// en HKCU\...\Run: nombre de la clave, nombre del valor, y cómo construir el valor a partir de
/// la ruta del ejecutable. La capa que toca el registro de verdad es
/// <c>AudioTranscriber.App.AutoStartHelper</c>; esta clase existe para poder testear la
/// construcción del valor sin depender de Windows.
/// </summary>
public static class AutoStartRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "AudioTranscriber";

    /// <summary>
    /// Command-line flag appended to the registered auto-start command. Read back in
    /// App.xaml.cs (via <see cref="StartupWindowMode.ShouldStartMinimized"/>) so a launch coming
    /// from Windows auto-start starts minimized to the tray instead of as a normal window — see
    /// changelog 2026-07-09 for the bug this fixes.
    /// </summary>
    public const string MinimizedArg = "--minimized";

    /// <summary>
    /// Arma el valor a escribir en el registro para <paramref name="exePath"/>: entre comillas
    /// si la ruta tiene espacios (necesario para que Windows la interprete como un único
    /// argumento al arrancar), tal cual si no, seguido siempre de <see cref="MinimizedArg"/> (así
    /// Windows le pasa ese flag a la app cada vez que la arranca por auto-start). Null si no hay
    /// ruta válida (nada que escribir).
    /// </summary>
    public static string? BuildRegistryValue(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
            return null;

        var quotedPath = exePath.Contains(' ') ? $"\"{exePath}\"" : exePath;
        return $"{quotedPath} {MinimizedArg}";
    }
}
