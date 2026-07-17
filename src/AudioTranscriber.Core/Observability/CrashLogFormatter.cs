using System.Text;

namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Lógica pura (sin I/O) del log de crashes local: formatea una entrada de excepción y resuelve la
/// carpeta/archivo donde debe escribirse. Separado a propósito del punto de escritura real (ver
/// CrashLogger en AudioTranscriber.App) para poder testear el formato y el path sin tocar el
/// filesystem. Es un log DISTINTO de error.log (ver App.xaml.cs LogError, que solo cubre
/// DispatcherUnhandledException): este cubre además AppDomain.UnhandledException y
/// TaskScheduler.UnobservedTaskException, un archivo por día, append-only.
/// </summary>
public static class CrashLogFormatter
{
    private const string AppFolderName = "AudioTranscriber";
    private const string LogsFolderName = "logs";

    /// <summary>Carpeta de logs de crash: {localAppData}\AudioTranscriber\logs</summary>
    public static string ResolveLogDirectory(string localAppDataPath) =>
        Path.Combine(localAppDataPath, AppFolderName, LogsFolderName);

    /// <summary>Nombre del archivo del día: crash-yyyyMMdd.log</summary>
    public static string ResolveLogFileName(DateTime date) => $"crash-{date:yyyyMMdd}.log";

    /// <summary>Ruta completa del archivo de log de crash para la fecha del timestamp dado.</summary>
    public static string ResolveLogFilePath(string localAppDataPath, DateTime timestamp) =>
        Path.Combine(ResolveLogDirectory(localAppDataPath), ResolveLogFileName(timestamp));

    /// <summary>
    /// Formatea una entrada de log: timestamp, versión de assembly, y tipo/mensaje/stack trace de
    /// la excepción, incluyendo excepciones internas encadenadas (InnerException). Termina con una
    /// línea en blanco extra para separar entradas consecutivas en el archivo append-only.
    /// </summary>
    public static string FormatEntry(DateTime timestamp, string assemblyVersion, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var sb = new StringBuilder();
        sb.Append('[').Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
        sb.Append("Versión: ").Append(assemblyVersion).Append('\n');

        Exception? current = exception;
        var depth = 0;
        while (current is not null)
        {
            if (depth > 0)
                sb.Append("--- Inner exception (nivel ").Append(depth).Append(") ---\n");

            sb.Append("Tipo: ").Append(current.GetType().FullName).Append('\n');
            sb.Append("Mensaje: ").Append(current.Message).Append('\n');
            sb.Append("Stack trace:\n").Append(current.StackTrace ?? "(sin stack trace)").Append('\n');

            current = current.InnerException;
            depth++;
        }

        sb.Append('\n');
        return sb.ToString();
    }
}
