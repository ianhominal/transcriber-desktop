namespace AudioTranscriber.Core.WebImport;

/// <summary>
/// Clasifica un yt-dlp que terminó con exit code distinto de 0 en un <see cref="WebImportStatus"/> +
/// mensaje en español rioplatense neutro, mirando el texto de stderr. Lógica pura (mismo criterio
/// que <c>SyncErrorClassifier</c>): sin I/O, solo texto de entrada.
/// <para/>
/// Los patrones de texto están tomados de los mensajes reales que imprime yt-dlp (en inglés,
/// siempre -- no tiene localización). Si yt-dlp cambia la redacción de un mensaje en una versión
/// nueva, esta clasificación puede dejar de reconocerlo y cae al genérico
/// <see cref="WebImportStatus.YtDlpFailed"/> (con el detalle crudo igual visible en el mensaje) --
/// nunca revienta, en el peor caso el mensaje es menos preciso.
/// </summary>
public static class YtDlpErrorClassifier
{
    public static (WebImportStatus Status, string Message) Classify(int exitCode, string? standardError)
    {
        var text = standardError ?? string.Empty;

        if (RequiresLogin(text))
            return (WebImportStatus.RequiresLogin, "El video es privado o requiere iniciar sesión para verlo.");

        if (IsUnsupportedUrl(text))
            return (WebImportStatus.InvalidUrl, "La URL no es válida o no corresponde a un sitio soportado.");

        if (IsGone(text))
            return (WebImportStatus.NoAudioFound, "Ese contenido ya no está disponible (puede haber sido eliminado).");

        if (IsNoAudio(text))
            return (WebImportStatus.NoAudioFound, "No se encontró audio en esa página.");

        if (IsNetworkError(text))
            return (WebImportStatus.NoConnection, "Sin conexión a internet. Revisá tu conexión e intentá de nuevo.");

        return (WebImportStatus.YtDlpFailed, $"Falló yt-dlp (código {exitCode}): {Summarize(text)}");
    }

    private static bool RequiresLogin(string text) =>
        Contains(text, "private video")
        || Contains(text, "sign in to confirm")
        || Contains(text, "members-only")
        || Contains(text, "login required")
        || Contains(text, "this video is available for registered users")
        || Contains(text, "requires membership")
        || (Contains(text, "available to") && Contains(text, "members"));

    private static bool IsUnsupportedUrl(string text) =>
        Contains(text, "unsupported url");

    private static bool IsNoAudio(string text) =>
        Contains(text, "no video formats found")
        || Contains(text, "requested format is not available")
        || Contains(text, "unable to extract")
        || Contains(text, "no media found")
        || Contains(text, "no video could be found");

    /// <summary>
    /// Contenido que existió pero ya no está (video borrado, dado de baja, 404 de la página).
    /// Se evalúa ANTES que la red porque yt-dlp escribe "Unable to download webpage: HTTP Error
    /// 404" — el mismo prefijo que un fallo de conexión real.
    /// </summary>
    private static bool IsGone(string text) =>
        Contains(text, "video unavailable")
        || Contains(text, "this video has been removed")
        || Contains(text, "no longer available")
        || Contains(text, "http error 404")
        || Contains(text, "http error 410");

    /// <summary>
    /// Fallos de RED de verdad. Ojo con "unable to download webpage": yt-dlp lo usa tanto para "no
    /// hay internet" como para "el servidor respondió 404". Por eso exige que aparezca junto a un
    /// síntoma real de conectividad (DNS, socket, timeout) en vez de alcanzar por sí solo.
    ///
    /// Esto ya se pagó caro una vez en el repo web (gotcha del CLAUDE.md): un mensaje fijo de
    /// "revisá tu conexión" mandó a un usuario a revisar el router durante horas cuando el problema
    /// era un 429 de GitHub. Decirle a alguien que su internet anda mal cuando anda bien no es solo
    /// inútil: lo manda a perder el tiempo en el lugar equivocado.
    /// </summary>
    private static bool IsNetworkError(string text) =>
        Contains(text, "failed to resolve")
        || Contains(text, "getaddrinfo failed")
        || Contains(text, "network is unreachable")
        || Contains(text, "connection refused")
        || Contains(text, "temporary failure in name resolution")
        || Contains(text, "urlopen error")
        || Contains(text, "connection timed out");

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Primera línea no vacía de stderr, para no volcarle a la usuaria el traceback completo de yt-dlp.</summary>
    private static string Summarize(string text)
    {
        var firstLine = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? "error desconocido." : firstLine;
    }
}
