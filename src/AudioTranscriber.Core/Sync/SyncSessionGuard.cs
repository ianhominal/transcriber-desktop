namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Lógica pura para distinguir un logout real (refresh token definitivamente inválido) de una
/// carrera entre un ciclo de sync viejo -que arrancó con un refresh token que mientras tanto
/// cambió- y un login nuevo que el usuario hizo mientras ese ciclo todavía corría. Sin esto, el
/// 400 de Supabase del ciclo viejo terminaba borrando una sesión nueva recién iniciada (bug: la
/// UI seguía diciendo "no hay sesión" justo después de loguearse). Sin I/O ni reloj, para poder
/// testearla sin mocks (mismo criterio que <see cref="TokenExpiryPolicy"/>).
/// </summary>
public static class SyncSessionGuard
{
    /// <summary>
    /// True si <paramref name="currentRefreshToken"/> (el que hay guardado AHORA en el store de
    /// secretos) ya no es el mismo que <paramref name="failedRefreshToken"/> (el que se usó en el
    /// ciclo de sync que acaba de fallar con 400): significa que hubo un login/refresh más nuevo
    /// de por medio mientras el ciclo viejo corría, y la sesión actual NO debe borrarse.
    /// </summary>
    public static bool WasSupersededByNewerLogin(string? currentRefreshToken, string? failedRefreshToken) =>
        !string.IsNullOrEmpty(currentRefreshToken)
        && !string.Equals(currentRefreshToken, failedRefreshToken, StringComparison.Ordinal);
}
