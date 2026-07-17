using System.IO;
using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.App;

/// <summary>
/// Wrapper delgado sobre <see cref="StartupLogFormatter"/> (AudioTranscriber.Core) que hace el
/// único I/O real: crea la carpeta de logs si falta y appendea la entrada ya formateada. Mismo
/// patrón que <see cref="TrayIconLogger"/>/<see cref="CrashLogger"/> -- un archivo por día en
/// %LOCALAPPDATA%\AudioTranscriber\logs\startup-yyyyMMdd.log. Se llama desde CADA paso del arranque
/// (<see cref="App.Main"/> y <see cref="App.OnStartup"/>), incluso ANTES de que exista una instancia
/// de <see cref="App"/> (y por lo tanto antes de que DispatcherUnhandledException/
/// AppDomain.UnhandledException estén suscriptos -- ver el comentario en Main), para no volver a
/// quedar ciegos si algo del arranque falla en silencio (bug 2026-07-08: la app sincronizaba pero no
/// mostraba ventana ni bandeja, sin ningún log ni crash). Nunca debe poder tirar una excepción
/// propia -- todo el cuerpo va wrapped en try/catch, igual que el resto de los *Logger de este
/// proyecto.
/// </summary>
internal static class StartupLogger
{
    public static void Log(string message)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var now = DateTime.Now;

            Directory.CreateDirectory(StartupLogFormatter.ResolveLogDirectory(localAppData));

            var path = StartupLogFormatter.ResolveLogFilePath(localAppData, now);
            var entry = StartupLogFormatter.FormatEntry(now, message);

            File.AppendAllText(path, entry);
        }
        catch { /* si ni siquiera se puede loguear, no hay más que hacer */ }
    }
}
