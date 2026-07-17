using AudioTranscriber.Core.Runtime;

namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Lógica pura (sin I/O) del log de diagnóstico del flujo de cierre de MainWindow. Instrumentación
/// agregada tras un bug reportado donde la app cerraba de verdad al tocar la [x] a pesar de tener
/// el toggle "Minimizar a la bandeja al cerrar" activado (ver changelog 2026-07-08): registra, en
/// cada intento de cierre, el valor del setting, si el cierre vino de "Salir" (exitRequested) y la
/// <see cref="WindowCloseAction"/> que resolvió <see cref="WindowCloseBehavior.Resolve"/> para ese
/// intento — así, si no se reproduce en desarrollo, el próximo reporte del usuario lo deja atrapado
/// en el log. Separado del punto de escritura real (ver CloseFlowLogger en AudioTranscriber.App)
/// para poder testear formato y paths sin tocar el filesystem, mismo criterio que CrashLogFormatter.
/// Comparte la carpeta de logs del crash log, pero un archivo del día DISTINTO (close-yyyyMMdd.log):
/// esto no es una excepción, es tracing de una decisión normal.
/// </summary>
public static class CloseFlowLogFormatter
{
    private const string AppFolderName = "AudioTranscriber";
    private const string LogsFolderName = "logs";

    /// <summary>Carpeta de logs (misma que el crash log): {localAppData}\AudioTranscriber\logs</summary>
    public static string ResolveLogDirectory(string localAppDataPath) =>
        Path.Combine(localAppDataPath, AppFolderName, LogsFolderName);

    /// <summary>Nombre del archivo del día: close-yyyyMMdd.log</summary>
    public static string ResolveLogFileName(DateTime date) => $"close-{date:yyyyMMdd}.log";

    /// <summary>Ruta completa del archivo de log de cierre para la fecha del timestamp dado.</summary>
    public static string ResolveLogFilePath(string localAppDataPath, DateTime timestamp) =>
        Path.Combine(ResolveLogDirectory(localAppDataPath), ResolveLogFileName(timestamp));

    /// <summary>
    /// Formatea una entrada de log de un intento de cierre: timestamp, el setting leído, si vino de
    /// "Salir" y la acción resuelta. Una línea por intento (append-only).
    /// </summary>
    public static string FormatEntry(
        DateTime timestamp, bool minimizeToTrayOnClose, bool exitRequested, WindowCloseAction action) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss}] MinimizeToTrayOnClose={minimizeToTrayOnClose} " +
        $"exitRequested={exitRequested} action={action}\n";
}
