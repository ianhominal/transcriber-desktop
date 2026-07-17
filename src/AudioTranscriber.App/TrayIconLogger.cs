using System.IO;
using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.App;

/// <summary>
/// Wrapper delgado sobre <see cref="TrayIconLogFormatter"/> (AudioTranscriber.Core) que hace el
/// único I/O real: crea la carpeta de logs si falta y appendea la entrada ya formateada. Mismo
/// patrón que <see cref="CrashLogger"/>/<see cref="CloseFlowLogger"/> -- un archivo por día en
/// %LOCALAPPDATA%\AudioTranscriber\logs\tray-yyyyMMdd.log. Pensado para diagnosticar el bug
/// reportado 2026-07-08 (el ícono de la bandeja no aparece, sin crash log) en la PC del usuario:
/// registra CADA intento de cargar/crear el ícono, no solo cuando algo tira excepción. Nunca debe
/// poder tirar una excepción propia — todo el cuerpo va wrapped en try/catch.
/// </summary>
internal static class TrayIconLogger
{
    public static void Log(string message)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var now = DateTime.Now;

            Directory.CreateDirectory(TrayIconLogFormatter.ResolveLogDirectory(localAppData));

            var path = TrayIconLogFormatter.ResolveLogFilePath(localAppData, now);
            var entry = TrayIconLogFormatter.FormatEntry(now, message);

            File.AppendAllText(path, entry);
        }
        catch { /* si ni siquiera se puede loguear, no hay más que hacer */ }
    }
}
