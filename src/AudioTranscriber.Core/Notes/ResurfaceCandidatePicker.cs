namespace AudioTranscriber.Core.Notes;

/// <summary>
/// Utilidades PURAS para el "resurfacing" de notas viejas en el dashboard NATIVO (paridad con
/// <c>src/lib/resurface.ts</c> del backend web, brief "Híbrido nativo" 2026-07-14: "una card
/// discreta ... con una transcripción vieja de las ya sincronizadas localmente"). A diferencia de
/// la web (que tiene <c>created_at</c> en la base), la app WPF no trackea fecha de captura por
/// separado -- el caller (<c>MainViewModel</c>) aproxima <see cref="ResurfaceCandidate.CreatedAt"/>
/// con la fecha de creación del .txt de transcript en disco, mismo criterio de "menor esfuerzo
/// razonable" que la web documenta para su propia limitación (no hay <c>last_opened_at</c>).
/// SIN endpoint nuevo: 100% cálculo local sobre transcripciones ya sincronizadas.
/// </summary>
public static class ResurfaceCandidatePicker
{
    private const double DayMs = 24 * 60 * 60 * 1000;

    /// <summary>Antigüedad mínima (en días) para que una nota sea candidata a resurfacing -- mismo valor que <c>RESURFACE_MIN_AGE_DAYS</c> de la web.</summary>
    public const int MinAgeDays = 14;

    /// <summary>true si <paramref name="createdAt"/> es lo bastante vieja como para ser candidata a resurfacing. <paramref name="now"/> inyectable para tests determinísticos.</summary>
    public static bool IsEligible(DateTime createdAt, DateTime now) =>
        (now - createdAt).TotalDays >= MinAgeDays;

    /// <summary>
    /// Elige, de forma pura y determinística, qué nota "resurfacear" entre las candidatas ya
    /// elegibles (el caller ya filtró por <see cref="IsEligible"/>). Devuelve la más VIEJA que no
    /// esté en <paramref name="excludeIds"/> -- notas ya descartadas por la usuaria (persistidas en
    /// <c>AppSettings</c>, equivalente nativo del <c>localStorage</c> que usa la web). Null si no
    /// queda ninguna candidata.
    /// </summary>
    public static ResurfaceCandidate? PickCandidate(
        IReadOnlyList<ResurfaceCandidate> candidates, IReadOnlySet<string> excludeIds)
    {
        ResurfaceCandidate? oldest = null;
        foreach (var candidate in candidates)
        {
            if (excludeIds.Contains(candidate.Id))
                continue;
            if (oldest is null || candidate.CreatedAt < oldest.CreatedAt)
                oldest = candidate;
        }
        return oldest;
    }

    /// <summary>
    /// Texto de tiempo relativo en español rioplatense neutro ("hoy", "hace 1 día", "hace 3
    /// semanas"...), mismo criterio que <c>formatRelativeTime</c> de la web. <paramref name="now"/>
    /// inyectable para tests determinísticos. Clampea a 0 (reloj desincronizado no debería mostrar
    /// tiempo negativo).
    /// </summary>
    public static string FormatRelativeTime(DateTime createdAt, DateTime now)
    {
        var diffMs = Math.Max(0, (now - createdAt).TotalMilliseconds);
        var days = (int)(diffMs / DayMs);

        if (days < 1) return "hoy";
        if (days == 1) return "hace 1 día";
        if (days < 7) return $"hace {days} días";

        var weeks = days / 7;
        if (days < 30) return weeks == 1 ? "hace 1 semana" : $"hace {weeks} semanas";

        var months = days / 30;
        if (days < 365) return months == 1 ? "hace 1 mes" : $"hace {months} meses";

        var years = days / 365;
        return years == 1 ? "hace 1 año" : $"hace {years} años";
    }
}

/// <summary>Una nota candidata a resurfacing (ya scopeada/filtrada por el caller). <see cref="Id"/>
/// identifica la nota localmente (mismo id usado para persistir descartes) y <see cref="Title"/> es
/// el texto a mostrar en la card.</summary>
public sealed record ResurfaceCandidate(string Id, string Title, DateTime CreatedAt);
