namespace AudioTranscriber.App;

/// <summary>
/// DSN de Sentry (reporte de errores/crashes). Para activarlo:
/// <list type="number">
/// <item>Creá (o abrí) un proyecto ".NET" en <c>https://sentry.io</c>.</item>
/// <item>Andá a Settings del proyecto → Client Keys (DSN).</item>
/// <item>Copiá el DSN (formato <c>https://&lt;public_key&gt;@&lt;org&gt;.ingest.sentry.io/&lt;project_id&gt;</c>)
/// y pegalo acá abajo, reemplazando el placeholder.</item>
/// </list>
/// Mientras <see cref="Dsn"/> quede como
/// <see cref="AudioTranscriber.Core.Observability.SentryConfig.PlaceholderDsn"/> (o vacío), Sentry
/// queda desactivado y la app arranca y funciona exactamente igual — ver
/// <see cref="SentryBootstrap.Init"/>. El DSN NO es secreto en el sentido de autenticación (solo
/// permite ENVIAR eventos a este proyecto, no leerlos), así que no hace falta cifrarlo como los
/// tokens de <see cref="SecureStore"/>; igual conviene no publicarlo innecesariamente para evitar
/// spam de eventos falsos.
/// </summary>
public static class SentrySettings
{
    public const string Dsn = "SENTRY_DSN";
}
