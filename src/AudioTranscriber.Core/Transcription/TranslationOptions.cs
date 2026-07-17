namespace AudioTranscriber.Core.Transcription;

/// <summary>
/// Idiomas destino para "Transcribir y traducir" (modo <c>translate</c> de
/// <c>POST /api/transcribe</c>) -- MISMA allowlist/labels/default que
/// <c>audio-transcriber-web/src/lib/translate/languages.ts</c> (<c>TRANSLATION_LANGUAGES</c> /
/// <c>DEFAULT_TRANSLATION_LANGUAGE</c>), verificado contra ese archivo antes de escribir esta clase.
/// El backend YA valida/resuelve estos mismos valores server-side (<c>resolveTranslationLanguage</c>),
/// así que esto es defensa en profundidad + fuente para el combo de la UI, no la única validación.
/// </summary>
public static class TranslationOptions
{
    public sealed record Language(string Code, string Label);

    public static readonly IReadOnlyList<Language> Languages = new[]
    {
        new Language("es", "Español"),
        new Language("en", "Inglés"),
        new Language("pt", "Portugués"),
        new Language("fr", "Francés"),
        new Language("it", "Italiano"),
        new Language("de", "Alemán"),
    };

    /// <summary>Inglés por default -- mismo criterio que la web: traducir "es a es" no tendría sentido.</summary>
    public const string DefaultLanguage = "en";

    private static readonly HashSet<string> AllowedCodes =
        new(Languages.Select(l => l.Code), StringComparer.Ordinal);

    /// <summary>Valida el código pedido contra la allowlist; cualquier otro valor cae al default.</summary>
    public static string ResolveLanguage(string? requested) =>
        requested is not null && AllowedCodes.Contains(requested) ? requested : DefaultLanguage;

    /// <summary>Modos de transcripción: "transcribe" (normal) o "translate" (transcribe + traduce el texto).</summary>
    public const string ModeTranscribe = "transcribe";
    public const string ModeTranslate = "translate";

    public static bool IsTranslateMode(string? mode) =>
        string.Equals(mode, ModeTranslate, StringComparison.OrdinalIgnoreCase);
}
