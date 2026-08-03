using System.Globalization;

namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Lógica pura (sin I/O) del log de diagnóstico de la transcripción.
///
/// Por qué existe: hasta ahora la transcripción era el ÚNICO subsistema sin log a disco — el
/// <c>onLog</c> de <c>TranscriptionService</c> iba solo a la pantalla. Cuando una tester reportó
/// "no me deja transcribir ningún audio", no había absolutamente nada que leer: ni qué archivo, ni
/// en qué fase, ni con qué error. Todo el diagnóstico dependía de que ella describiera lo que veía.
///
/// Mismo criterio que <see cref="CloseFlowLogFormatter"/>/<see cref="CrashLogFormatter"/>: formato y
/// rutas acá (testeables sin tocar el filesystem), la escritura real en <c>TranscriptionLogger</c>
/// (AudioTranscriber.App). Comparte la carpeta de logs, con un archivo del día propio.
/// </summary>
public static class TranscriptionLogFormatter
{
    private const string AppFolderName = "AudioTranscriber";
    private const string LogsFolderName = "logs";

    /// <summary>Carpeta de logs (la misma que el resto): {localAppData}\AudioTranscriber\logs</summary>
    public static string ResolveLogDirectory(string localAppDataPath) =>
        Path.Combine(localAppDataPath, AppFolderName, LogsFolderName);

    /// <summary>Nombre del archivo del día: transcribe-yyyyMMdd.log</summary>
    public static string ResolveLogFileName(DateTime date) => $"transcribe-{date:yyyyMMdd}.log";

    /// <summary>Ruta completa del log de transcripción para la fecha del timestamp dado.</summary>
    public static string ResolveLogFilePath(string localAppDataPath, DateTime timestamp) =>
        Path.Combine(ResolveLogDirectory(localAppDataPath), ResolveLogFileName(timestamp));

    /// <summary>
    /// Encabezado de un intento: todo el contexto que después se necesita para entender el resto de
    /// las líneas. Va con una línea en blanco antes para que cada intento se distinga a simple vista.
    /// </summary>
    public static string FormatStart(
        DateTime timestamp,
        string fileName,
        long sizeBytes,
        string engine,
        string model,
        string language,
        bool diarization,
        string appVersion)
    {
        var mb = (sizeBytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture);
        return $"\n[{timestamp:yyyy-MM-dd HH:mm:ss}] === INICIO === archivo='{fileName}' " +
               $"tamaño={mb} MB extensión='{ExtensionOf(fileName)}' motor='{engine}' modelo='{model}' " +
               $"idioma='{language}' identificarHablantes={diarization} versión={appVersion}\n";
    }

    /// <summary>Un paso del proceso (los mismos mensajes que ya se muestran en pantalla).</summary>
    public static string FormatStep(DateTime timestamp, string message) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss}] {message}\n";

    /// <summary>Cierre exitoso, con cuánto tardó y cuánto texto salió.</summary>
    public static string FormatSuccess(DateTime timestamp, TimeSpan elapsed, int characters) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss}] === OK === duración={Seconds(elapsed)}s " +
        $"caracteres={characters}\n";

    /// <summary>Cancelación pedida por la persona: no es un error, pero importa distinguirla.</summary>
    public static string FormatCancelled(DateTime timestamp, TimeSpan elapsed) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss}] === CANCELADO === duración={Seconds(elapsed)}s\n";

    // Cultura invariante en TODOS los números del log: en una máquina en español "42.5" se formatea
    // como "42,5", y un log que cambia de forma según la configuración regional de quien lo genera
    // es más difícil de leer y de comparar entre reportes.
    private static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Fallo, con el tipo de excepción, el mensaje, la inner y el stack. Completo a propósito: es
    /// justo lo que faltaba para diagnosticar sin tener la máquina delante.
    /// </summary>
    public static string FormatFailure(DateTime timestamp, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var inner = ex.InnerException is { } i ? $"\n  inner: {i.GetType().Name}: {i.Message}" : string.Empty;
        return $"[{timestamp:yyyy-MM-dd HH:mm:ss}] === ERROR === {ex.GetType().Name}: {ex.Message}{inner}\n" +
               $"  stack: {ex.StackTrace?.Replace("\n", "\n  ") ?? "(sin stack)"}\n";
    }

    private static string ExtensionOf(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? "(sin extensión)" : ext.ToLowerInvariant();
    }
}
