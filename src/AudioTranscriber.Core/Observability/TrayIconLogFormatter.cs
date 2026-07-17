namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Lógica pura (sin I/O) del log de diagnóstico del ícono de bandeja -- mismo patrón que
/// <see cref="CrashLogFormatter"/>/<see cref="CloseFlowLogFormatter"/>: resuelve carpeta/archivo y
/// formatea una línea, separado del punto de escritura real (<c>TrayIconLogger</c> en
/// AudioTranscriber.App) para poder testear formato y path sin tocar el filesystem.
/// <para/>
/// Motivación (bug real 2026-07-08): el usuario reportó que el ícono de la bandeja no aparece
/// (v1.0.13), sin ningún crash log -- <c>TrayIconService.LoadTrayIcon()</c> ya atrapa sus propias
/// excepciones y devuelve <c>null</c> en silencio (para nunca tirar abajo el arranque), así que no
/// había forma de saber, sin reproducirlo en la PC del usuario, si el ícono se cargó, si el stream
/// vino null, o si algo posterior (ForceCreate, la construcción del resto de TrayIconService) falló.
/// </summary>
public static class TrayIconLogFormatter
{
    private const string AppFolderName = "AudioTranscriber";
    private const string LogsFolderName = "logs";

    /// <summary>Carpeta de logs de bandeja: {localAppData}\AudioTranscriber\logs</summary>
    public static string ResolveLogDirectory(string localAppDataPath) =>
        Path.Combine(localAppDataPath, AppFolderName, LogsFolderName);

    /// <summary>Nombre del archivo del día: tray-yyyyMMdd.log</summary>
    public static string ResolveLogFileName(DateTime date) => $"tray-{date:yyyyMMdd}.log";

    /// <summary>Ruta completa del archivo de log de bandeja para la fecha del timestamp dado.</summary>
    public static string ResolveLogFilePath(string localAppDataPath, DateTime timestamp) =>
        Path.Combine(ResolveLogDirectory(localAppDataPath), ResolveLogFileName(timestamp));

    /// <summary>Formatea una línea de diagnóstico: timestamp + mensaje libre.</summary>
    public static string FormatEntry(DateTime timestamp, string message) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss}] {message}\n";
}
