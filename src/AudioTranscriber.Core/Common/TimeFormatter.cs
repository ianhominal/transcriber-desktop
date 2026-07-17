namespace AudioTranscriber.Core.Common;

/// <summary>
/// Formatea duraciones para mostrar en la UI (cronómetro de grabación, transcripción en vivo,
/// reproductor). Lógica pura, sin dependencias de reloj ni de UI.
/// </summary>
public static class TimeFormatter
{
    /// <summary>
    /// "mm:ss" si dura menos de una hora, "h:mm:ss" si dura una hora o más. Negativos se
    /// tratan como cero (un cronómetro nunca debería mostrar tiempo negativo).
    /// </summary>
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}
