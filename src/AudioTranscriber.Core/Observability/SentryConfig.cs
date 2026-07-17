namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Resuelve si Sentry debe quedar activo según el DSN configurado. Lógica pura y testeada: la usa
/// <c>SentryBootstrap</c> (AudioTranscriber.App) para decidir si inicializar el SDK. Sin un DSN
/// real (vacío o el placeholder sin reemplazar), Sentry queda desactivado y la app arranca y
/// funciona exactamente igual que sin esta integración — ver el comentario de
/// <c>SentrySettings.Dsn</c> en App para dónde pegar el DSN real de un proyecto de sentry.io.
/// </summary>
public static class SentryConfig
{
    /// <summary>Placeholder que el usuario debe reemplazar por el DSN real de su proyecto en sentry.io.</summary>
    public const string PlaceholderDsn = "SENTRY_DSN";

    /// <summary>True solo si <paramref name="dsn"/> es un valor no vacío distinto del placeholder.</summary>
    public static bool IsEnabled(string? dsn) =>
        !string.IsNullOrWhiteSpace(dsn)
        && !string.Equals(dsn.Trim(), PlaceholderDsn, StringComparison.OrdinalIgnoreCase);
}
