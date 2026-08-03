using System;
using System.IO;
using System.Reflection;
using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.App;

/// <summary>
/// Escribe el log de diagnóstico de la transcripción (formato y rutas en
/// <see cref="TranscriptionLogFormatter"/>, AudioTranscriber.Core).
///
/// Existe porque la transcripción era el único subsistema sin rastro en disco: una tester reportó
/// "no me deja transcribir ningún audio" y no había NADA para leer — ni qué archivo, ni en qué fase
/// se cortó, ni con qué error. Ahora cada intento queda registrado de punta a punta.
///
/// Nunca tira: si ni siquiera se puede loguear, no hay nada más que hacer, y un fallo del logger no
/// puede llevarse puesta una transcripción (mismo criterio que <see cref="CloseFlowLogger"/>).
/// </summary>
public static class TranscriptionLogger
{
    /// <summary>Carpeta donde viven TODOS los logs de la app — la que abre el botón de Configuración.</summary>
    public static string LogDirectory =>
        TranscriptionLogFormatter.ResolveLogDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Versión de la app, para saber contra qué build se está leyendo el log.</summary>
    private static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "desconocida";

    public static void Start(string fileName, long sizeBytes, string engine, string model, string language, bool diarization) =>
        Write(now => TranscriptionLogFormatter.FormatStart(
            now, fileName, sizeBytes, engine, model, language, diarization, AppVersion));

    public static void Step(string message) =>
        Write(now => TranscriptionLogFormatter.FormatStep(now, message));

    public static void Success(TimeSpan elapsed, int characters) =>
        Write(now => TranscriptionLogFormatter.FormatSuccess(now, elapsed, characters));

    public static void Cancelled(TimeSpan elapsed) =>
        Write(now => TranscriptionLogFormatter.FormatCancelled(now, elapsed));

    public static void Failure(Exception ex) =>
        Write(now => TranscriptionLogFormatter.FormatFailure(now, ex));

    private static void Write(Func<DateTime, string> entry)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var now = DateTime.Now;
            Directory.CreateDirectory(TranscriptionLogFormatter.ResolveLogDirectory(localAppData));
            File.AppendAllText(TranscriptionLogFormatter.ResolveLogFilePath(localAppData, now), entry(now));
        }
        catch
        {
            // Si ni siquiera se puede loguear, no hay más que hacer. Nunca romper la transcripción.
        }
    }
}
