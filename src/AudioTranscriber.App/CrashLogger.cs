using System.IO;
using System.Reflection;
using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.App;

/// <summary>
/// Wrapper delgado sobre <see cref="CrashLogFormatter"/> (AudioTranscriber.Core) que hace el único
/// I/O real: crea la carpeta de logs si falta y appendea la entrada ya formateada. Log DISTINTO de
/// error.log (ver App.xaml.cs LogError): cubre las tres fuentes de excepciones no manejadas
/// (DispatcherUnhandledException, AppDomain.UnhandledException, TaskScheduler.UnobservedTaskException),
/// un archivo por día en %LOCALAPPDATA%\AudioTranscriber\logs\crash-yyyyMMdd.log. Nunca debe poder
/// tirar una excepción propia — igual que LogError, todo el cuerpo va wrapped en try/catch.
/// </summary>
public static class CrashLogger
{
    public static void Log(Exception exception)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var now = DateTime.Now;

            Directory.CreateDirectory(CrashLogFormatter.ResolveLogDirectory(localAppData));

            var path = CrashLogFormatter.ResolveLogFilePath(localAppData, now);
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "desconocida";
            var entry = CrashLogFormatter.FormatEntry(now, version, exception);

            File.AppendAllText(path, entry);
        }
        catch { /* si ni siquiera se puede loguear, no hay más que hacer */ }
    }

    /// <summary>
    /// Carpeta de logs de crash, creándola si todavía no existe. La usa el ítem de menú "Abrir
    /// carpeta de logs" de la bandeja (ver TrayIconService) para poder abrirla aunque todavía no
    /// haya ningún crash registrado.
    /// </summary>
    public static string EnsureLogDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = CrashLogFormatter.ResolveLogDirectory(localAppData);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
