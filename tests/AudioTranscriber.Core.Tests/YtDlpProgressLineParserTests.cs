using AudioTranscriber.Core.WebImport;

namespace AudioTranscriber.Core.Tests;

public class YtDlpProgressLineParserTests
{
    [Theory]
    [InlineData("[download]  42.3% of 10.00MiB at 1.20MiB/s ETA 00:05", 42.3)]
    [InlineData("[download] 100% of 3.50MiB in 00:02", 100)]
    [InlineData("[download]   0.0% of 8.12MiB at Unknown B/s ETA Unknown", 0)]
    public void Reconoce_el_porcentaje_de_lineas_de_progreso_reales(string line, double expected)
    {
        var ok = YtDlpProgressLineParser.TryParse(line, out var percent);

        Assert.True(ok);
        Assert.Equal(expected, percent, precision: 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[youtube] abc123: Downloading webpage")]
    [InlineData("[Merger] Merging formats into \"video.mp4\"")]
    public void Lineas_sin_porcentaje_no_matchean(string? line)
    {
        var ok = YtDlpProgressLineParser.TryParse(line, out var percent);

        Assert.False(ok);
        Assert.Equal(0, percent);
    }
}
