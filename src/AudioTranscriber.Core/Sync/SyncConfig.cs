namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Configuración pública del backend de sync. Estos valores NO son secretos: la URL del
/// backend y la anon key de Supabase están pensadas para viajar en el cliente (protegidas por
/// RLS del lado del servidor). La key de Groq NUNCA vive acá: la transcripción corre
/// server-side (ver 07-diseno-cliente-sync.md).
/// </summary>
public static class SyncConfig
{
    public const string BackendBaseUrl = "https://audio-transcriber-web-kappa.vercel.app";
    public const string SupabaseUrl = "https://vxlbvvtgdkxaktdiepow.supabase.co";
    public const string SupabaseAnonKey = "sb_publishable_75O4HCdvfV_2yXUIV7RXAQ_PoM8Ztnk";

    /// <summary>
    /// Versión del PROTOCOLO de sync (ADR-07g, design.md) -- NO es la versión del producto
    /// (<c>AudioTranscriber.App.csproj</c>, hoy 1.0.60). Viaja en el header "x-client-version" de
    /// cada pull/push; el backend la valida contra <c>MIN_SYNC_CLIENT_VERSION</c>
    /// (web/src/lib/sync/pushConflict.ts) para rechazar un desktop viejo que pushee sin
    /// <c>base_version</c> -- ver <see cref="SyncApiClient"/>. Solo sube cuando el CONTRATO de
    /// sync cambia de forma incompatible, no en cada release del producto.
    /// </summary>
    public const string ClientVersion = "2.0.0";
}
