using System.IO;
using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.App;

/// <summary>
/// Wrapper delgado sobre <see cref="UpdateLogFormatter"/> (AudioTranscriber.Core) que hace el único
/// I/O real: crea la carpeta de logs si falta y appendea la entrada ya formateada. Mismo patrón que
/// <see cref="StartupLogger"/>/<see cref="CrashLogger"/> -- un archivo por día en
/// %LOCALAPPDATA%\AudioTranscriber\logs\update-yyyyMMdd.log. Se llama desde CADA paso relevante de
/// <see cref="UpdateService"/> (chequeo, descarga, y el intento de aplicar/reiniciar), incluso
/// cuando el resultado es silencioso para el usuario (ver <see cref="UpdateService.CheckAndDownloadAsync"/>),
/// para no quedar ciegos ante el próximo reporte de "no busca actualizaciones o falla". Nunca debe
/// poder tirar una excepción propia -- todo el cuerpo va wrapped en try/catch, igual que el resto de
/// los *Logger de este proyecto.
/// </summary>
internal static class UpdateLogger
{
    public static void Log(string message)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var now = DateTime.Now;

            Directory.CreateDirectory(UpdateLogFormatter.ResolveLogDirectory(localAppData));

            var path = UpdateLogFormatter.ResolveLogFilePath(localAppData, now);
            var entry = UpdateLogFormatter.FormatEntry(now, message);

            File.AppendAllText(path, entry);
        }
        catch { /* si ni siquiera se puede loguear, no hay más que hacer */ }
    }
}
