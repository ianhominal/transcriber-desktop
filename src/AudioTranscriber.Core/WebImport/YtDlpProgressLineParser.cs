using System.Globalization;
using System.Text.RegularExpressions;

namespace AudioTranscriber.Core.WebImport;

/// <summary>
/// Extrae el porcentaje de avance de una línea de stdout de <c>yt-dlp</c> durante una descarga
/// (formato típico: <c>[download]  42.3% of 10.00MiB at 1.20MiB/s ETA 00:05</c>). Lógica pura sobre
/// texto -- <see cref="WebAudioDownloader"/> la usa línea por línea a medida que llegan del proceso.
/// </summary>
public static partial class YtDlpProgressLineParser
{
    [GeneratedRegex(@"\[download\]\s+(\d{1,3}(?:\.\d+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    /// <summary>True si <paramref name="line"/> es una línea de progreso de descarga con porcentaje reconocible.</summary>
    public static bool TryParse(string? line, out double percent)
    {
        percent = 0;
        if (string.IsNullOrEmpty(line))
            return false;

        var match = PercentRegex().Match(line);
        if (!match.Success)
            return false;

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
            return false;

        percent = Math.Clamp(percent, 0, 100);
        return true;
    }
}
