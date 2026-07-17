namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Lógica pura (sin I/O) del log de diagnóstico del auto-updater -- mismo patrón que
/// <see cref="StartupLogFormatter"/>/<see cref="CrashLogFormatter"/>: resuelve carpeta/archivo y
/// formatea una línea, separado del punto de escritura real (<c>UpdateLogger</c> en
/// AudioTranscriber.App) para poder testear formato y path sin tocar el filesystem.
/// <para/>
/// Motivación: el reporte "al abrir la app no busca actualizaciones o falla" no dejaba NINGÚN
/// rastro -- <c>UpdateService.CheckAndDownloadCoreAsync</c> silencia a propósito todas las
/// excepciones (sin conexión, GitHub caído, etc. no deben tirar abajo el chequeo automático), pero
/// eso también silenciaba fallas reales de la descarga o de <c>ApplyUpdatesAndRestart</c> sin dejar
/// forma de diagnosticarlas sin reproducirlas en la PC del usuario. Este log cubre cada paso del
/// chequeo/descarga/aplicación, uno por uno, igual que <see cref="StartupLogFormatter"/> cubre el
/// arranque.
/// </summary>
public static class UpdateLogFormatter
{
    private const string AppFolderName = "AudioTranscriber";
    private const string LogsFolderName = "logs";

    /// <summary>Carpeta de logs del updater: {localAppData}\AudioTranscriber\logs</summary>
    public static string ResolveLogDirectory(string localAppDataPath) =>
        Path.Combine(localAppDataPath, AppFolderName, LogsFolderName);

    /// <summary>Nombre del archivo del día: update-yyyyMMdd.log</summary>
    public static string ResolveLogFileName(DateTime date) => $"update-{date:yyyyMMdd}.log";

    /// <summary>Ruta completa del archivo de log del updater para la fecha del timestamp dado.</summary>
    public static string ResolveLogFilePath(string localAppDataPath, DateTime timestamp) =>
        Path.Combine(ResolveLogDirectory(localAppDataPath), ResolveLogFileName(timestamp));

    /// <summary>Formatea una línea de diagnóstico: timestamp + mensaje libre.</summary>
    public static string FormatEntry(DateTime timestamp, string message) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss}] {message}\n";
}
