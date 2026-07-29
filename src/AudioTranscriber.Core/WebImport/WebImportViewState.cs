namespace AudioTranscriber.Core.WebImport;

/// <summary>
/// Lógica pura de estado/formateo para la ventana "Transcribir desde una URL" (App/WebImportWindow):
/// gating de los botones ("Analizar" y "Descargar y transcribir") y formateo de duración de cada
/// <see cref="WebMediaItem"/> encontrado -- separado de la UI para poder testearlo sin WPF, mismo
/// criterio que <c>BatchTranscribePlanner</c>/<c>TranscribeGateFormatter</c> en Workspaces.
/// </summary>
public static class WebImportViewState
{
    /// <summary>Habilita "Analizar": hace falta algo de texto en el campo URL.</summary>
    public static bool CanAnalyze(string? url) => !string.IsNullOrWhiteSpace(url);

    /// <summary>Habilita "Descargar y transcribir": hace falta al menos un ítem tildado en la lista.</summary>
    public static bool CanConfirmSelection(int selectedCount) => selectedCount > 0;

    /// <summary>
    /// Formatea la duración de un ítem para la lista de resultados: "mm:ss" si dura menos de una
    /// hora, "h:mm:ss" si dura una hora o más (yt-dlp trae videos largos -- charlas, streams), y
    /// "--:--" cuando no vino duración (algunos extractores no la exponen bajo <c>--flat-playlist</c>)
    /// o vino negativa (defensivo, no debería pasar nunca en la práctica).
    /// </summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } d || d < TimeSpan.Zero)
            return "--:--";

        return d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d:mm\\:ss}"
            : d.ToString(@"mm\:ss");
    }
}
