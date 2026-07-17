using System.Reflection;
using AudioTranscriber.Core.Observability;
using Sentry;

namespace AudioTranscriber.App;

/// <summary>
/// Arranca Sentry (reporte de errores/crashes) si hay un DSN configurado (ver
/// <see cref="SentrySettings"/>). 100% opcional: sin DSN real, <see cref="Init"/> no hace nada y
/// devuelve un <see cref="IDisposable"/> vacío. El resto del código (App.xaml.cs,
/// <see cref="SyncCoordinator"/>) puede llamar a <c>SentrySdk.CaptureException</c>/
/// <c>AddBreadcrumb</c> sin chequear si Sentry está habilitado: el SDK deja esas llamadas como
/// no-op cuando <c>Init</c> nunca corrió (hub deshabilitado por defecto), así que no hace falta un
/// flag "IsEnabled" repartido por todo el código.
/// <para/>
/// Captura automática de excepciones no controladas del PROCESO: <c>AppDomain.UnhandledException</c>
/// y <c>TaskScheduler.UnobservedTaskException</c> ya vienen ENCENDIDAS por defecto dentro de
/// <c>SentrySdk.Init</c> (integraciones propias del SDK — ver
/// <c>SentryOptions.DisableAppDomainUnhandledExceptionCapture()</c> /
/// <c>DisableUnobservedTaskExceptionCapture()</c> en la doc oficial, que confirman que están
/// activas por default). No hace falta suscribirse a mano a esos dos eventos: hacerlo duplicaría el
/// reporte. Lo único WPF-específico que el SDK no cubre solo es
/// <c>Application.DispatcherUnhandledException</c>: ese hook ya existía en App.xaml.cs (red de
/// seguridad pre-Sentry, muestra el MessageBox y loguea a error.log) — ahí se agregó el
/// <c>SentrySdk.CaptureException</c> correspondiente.
/// </summary>
public static class SentryBootstrap
{
    public static IDisposable Init()
    {
        if (!SentryConfig.IsEnabled(SentrySettings.Dsn))
            return NoopDisposable.Instance;

        return SentrySdk.Init(options =>
        {
            options.Dsn = SentrySettings.Dsn;
            options.Release = $"AudioTranscriber@{ReleaseVersion}";
            options.Environment = EnvironmentName;

            // La app maneja tokens de sesión de Google/Supabase (ver SecureStore): nunca mandar
            // IP/headers/nombre de usuario por default. El scrubbing fino de Extra/Tags/mensajes
            // corre igual en SentryPiiFilter (SetBeforeSend/SetBeforeBreadcrumb más abajo).
            options.SendDefaultPii = false;

            // App de escritorio de un solo usuario por proceso (no un server multi-tenant).
            options.IsGlobalModeEnabled = true;

            options.SetBeforeSend((sentryEvent, _) => SentryPiiFilter.Apply(sentryEvent));
            options.SetBeforeBreadcrumb(SentryPiiFilter.Apply);
        });
    }

    private static string ReleaseVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

#if DEBUG
    private const string EnvironmentName = "development";
#else
    private const string EnvironmentName = "production";
#endif

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
