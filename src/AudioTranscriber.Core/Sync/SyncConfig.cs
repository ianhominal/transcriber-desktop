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
}
