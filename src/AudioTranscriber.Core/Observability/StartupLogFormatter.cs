namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Lógica pura (sin I/O) del log de diagnóstico del arranque -- mismo patrón que
/// <see cref="TrayIconLogFormatter"/>/<see cref="CrashLogFormatter"/>: resuelve carpeta/archivo y
/// formatea una línea, separado del punto de escritura real (<c>StartupLogger</c> en
/// AudioTranscriber.App) para poder testear formato y path sin tocar el filesystem.
/// <para/>
/// Motivación (bug 2026-07-08): la app instalada sincronizaba en segundo plano pero no mostraba
/// ni la ventana principal ni el ícono de bandeja, y no quedaba NINGÚN rastro (ni tray-log ni
/// crash-log ni error.log) de por qué. Antes de este log no había forma de saber, sin reproducirlo
/// en la PC del usuario, si <c>MainWindow</c> llegaba null a <c>App.OnStartup</c>, si algo antes de
/// <c>new App()</c> (Velopack, Sentry) moría en silencio, o si el problema estaba en otro lado. Este
/// log cubre esos pasos, uno por uno, desde la primera línea de <c>Main()</c>.
/// </summary>
public static class StartupLogFormatter
{
    private const string AppFolderName = "AudioTranscriber";
    private const string LogsFolderName = "logs";

    /// <summary>Carpeta de logs de arranque: {localAppData}\AudioTranscriber\logs</summary>
    public static string ResolveLogDirectory(string localAppDataPath) =>
        Path.Combine(localAppDataPath, AppFolderName, LogsFolderName);

    /// <summary>Nombre del archivo del día: startup-yyyyMMdd.log</summary>
    public static string ResolveLogFileName(DateTime date) => $"startup-{date:yyyyMMdd}.log";

    /// <summary>Ruta completa del archivo de log de arranque para la fecha del timestamp dado.</summary>
    public static string ResolveLogFilePath(string localAppDataPath, DateTime timestamp) =>
        Path.Combine(ResolveLogDirectory(localAppDataPath), ResolveLogFileName(timestamp));

    /// <summary>Formatea una línea de diagnóstico: timestamp + mensaje libre.</summary>
    public static string FormatEntry(DateTime timestamp, string message) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss}] {message}\n";
}
