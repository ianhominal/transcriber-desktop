using System.IO;
using AudioTranscriber.Core.Observability;
using AudioTranscriber.Core.Runtime;

namespace AudioTranscriber.App;

/// <summary>
/// Wrapper delgado sobre <see cref="CloseFlowLogFormatter"/> (AudioTranscriber.Core) que hace el
/// único I/O real: crea la carpeta de logs si falta y appendea la entrada ya formateada. Se llama
/// desde <see cref="App.OnMainWindowClosing"/> en CADA intento de cierre de MainWindow (no solo
/// ante error) — instrumentación agregada tras un bug reportado donde la app cerraba de verdad con
/// el toggle "Minimizar a la bandeja al cerrar" activado (ver changelog 2026-07-08). Nunca debe
/// poder tirar una excepción propia — igual que CrashLogger, todo el cuerpo va wrapped en try/catch.
/// </summary>
public static class CloseFlowLogger
{
    public static void Log(bool minimizeToTrayOnClose, bool exitRequested, WindowCloseAction action)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var now = DateTime.Now;

            Directory.CreateDirectory(CloseFlowLogFormatter.ResolveLogDirectory(localAppData));

            var path = CloseFlowLogFormatter.ResolveLogFilePath(localAppData, now);
            var entry = CloseFlowLogFormatter.FormatEntry(now, minimizeToTrayOnClose, exitRequested, action);

            File.AppendAllText(path, entry);
        }
        catch { /* si ni siquiera se puede loguear, no hay más que hacer */ }
    }
}
