namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Decide si conviene refrescar el access token antes de que expire, con un margen de
/// seguridad, para evitar que una llamada al backend falle a mitad de un ciclo de sync
/// por un token que vence justo en ese momento. Lógica pura (sin I/O ni reloj real) para
/// poder testearla sin mocks de HttpClient.
/// </summary>
public static class TokenExpiryPolicy
{
    /// <summary>Margen de seguridad por defecto antes del vencimiento real.</summary>
    public static readonly TimeSpan DefaultBuffer = TimeSpan.FromSeconds(60);

    /// <summary>
    /// True si el token ya venció, está por vencer dentro del margen de seguridad, o no hay
    /// dato de vencimiento (<paramref name="expiresAtUnixSeconds"/> &lt;= 0).
    /// <paramref name="expiresAtUnixSeconds"/> es el campo "expires_at" que devuelve Supabase
    /// Auth (segundos desde epoch UTC).
    /// </summary>
    public static bool ShouldRefresh(DateTimeOffset now, long expiresAtUnixSeconds, TimeSpan? buffer = null)
    {
        if (expiresAtUnixSeconds <= 0)
            return true;

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds);
        return now >= expiresAt - (buffer ?? DefaultBuffer);
    }
}
