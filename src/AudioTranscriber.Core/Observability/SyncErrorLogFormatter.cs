using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Lógica pura (sin I/O) del log de errores de sync: formatea una entrada y resuelve la
/// carpeta/archivo donde debe escribirse. Mismo patrón que <see cref="CrashLogFormatter"/> y
/// <see cref="CloseFlowLogFormatter"/> -- separado del punto de escritura real (SyncCoordinator en
/// AudioTranscriber.App) para poder testear formato y path sin tocar el filesystem. Antes el sync
/// logueaba a un único archivo acumulativo FUERA de la carpeta "logs"
/// ({LocalAppData}\AudioTranscriber\sync.log), inconsistente con crash-*.log/close-*.log y por eso
/// invisible para quien solo miraba esa carpeta; ahora sigue el mismo esquema por día que ya usan
/// crash/close.
/// </summary>
public static class SyncErrorLogFormatter
{
    private const string AppFolderName = "AudioTranscriber";
    private const string LogsFolderName = "logs";

    /// <summary>Carpeta de logs de sync: {localAppData}\AudioTranscriber\logs</summary>
    public static string ResolveLogDirectory(string localAppDataPath) =>
        Path.Combine(localAppDataPath, AppFolderName, LogsFolderName);

    /// <summary>Nombre del archivo del día: sync-yyyyMMdd.log</summary>
    public static string ResolveLogFileName(DateTime date) => $"sync-{date:yyyyMMdd}.log";

    /// <summary>Ruta completa del archivo de log de sync para la fecha del timestamp dado.</summary>
    public static string ResolveLogFilePath(string localAppDataPath, DateTime timestamp) =>
        Path.Combine(ResolveLogDirectory(localAppDataPath), ResolveLogFileName(timestamp));

    /// <summary>
    /// Formatea una entrada de log: timestamp, categoría clasificada (ver
    /// <see cref="SyncErrorClassifier"/>), y tipo/mensaje/stack trace de la excepción. NUNCA loguea
    /// tokens ni datos sensibles: solo usa lo que ya trae la excepción (tipo/mensaje/stack trace),
    /// que en este cliente es el status code + cuerpo de la RESPUESTA del backend
    /// (SyncApiException/SyncAuthException), no el access/refresh token que viajó en el request.
    /// Termina con una línea en blanco extra para separar entradas consecutivas en el archivo
    /// append-only (mismo criterio que CrashLogFormatter).
    /// </summary>
    public static string FormatEntry(DateTime timestamp, SyncErrorCategory category, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var sb = new StringBuilder();
        sb.Append('[').Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
        sb.Append("Categoría: ").Append(category).Append('\n');
        sb.Append("Tipo: ").Append(exception.GetType().FullName).Append('\n');
        sb.Append("Mensaje: ").Append(exception.Message).Append('\n');
        sb.Append("Stack trace:\n").Append(exception.StackTrace ?? "(sin stack trace)").Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }
}
