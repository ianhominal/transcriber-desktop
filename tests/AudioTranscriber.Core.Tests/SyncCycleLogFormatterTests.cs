using AudioTranscriber.Core.Observability;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncCycleLogFormatterTests
{
    private static readonly DateTime SampleTimestamp = new(2026, 7, 8, 12, 0, 0);

    [Fact]
    public void FormatEntry_IncluyeTimestampManualYOutcome()
    {
        var entry = SyncCycleLogFormatter.FormatEntry(
            SampleTimestamp, manual: true, SyncOutcome.Completed,
            pulledProjects: 0, pulledTranscriptions: 0, actionsApplied: 0, pushedCount: 0,
            audioDownloadFailures: 0, diagnostics: Array.Empty<string>());

        Assert.Contains("2026-07-08 12:00:00", entry);
        Assert.Contains("manual=True", entry);
        Assert.Contains("outcome=Completed", entry);
    }

    [Fact]
    public void FormatEntry_IncluyeConteosDePullYAcciones()
    {
        var entry = SyncCycleLogFormatter.FormatEntry(
            SampleTimestamp, manual: false, SyncOutcome.Completed,
            pulledProjects: 3, pulledTranscriptions: 12, actionsApplied: 2, pushedCount: 1,
            audioDownloadFailures: 1, diagnostics: Array.Empty<string>());

        Assert.Contains("3 proyecto(s)", entry);
        Assert.Contains("12 transcripción(es)", entry);
        Assert.Contains("Aplicadas: 2 acción(es)", entry);
        Assert.Contains("Pusheadas: 1", entry);
        Assert.Contains("Fallos de descarga de audio: 1", entry);
    }

    [Fact]
    public void FormatEntry_ListaCadaLineaDeDiagnostico()
    {
        var diagnostics = new[]
        {
            "transcripción 'nota.mp3': sin audio_url_signed -- guardada solo con texto.",
            "2 proyecto(s) y 5 transcripción(es) sin cambios (omitidas).",
        };

        var entry = SyncCycleLogFormatter.FormatEntry(
            SampleTimestamp, manual: false, SyncOutcome.Completed,
            0, 0, 0, 0, 0, diagnostics);

        foreach (var line in diagnostics)
            Assert.Contains(line, entry);
    }

    [Fact]
    public void FormatEntry_SinDiagnosticos_NoRompeYSigueTerminandoConLineaEnBlanco()
    {
        var entry = SyncCycleLogFormatter.FormatEntry(
            SampleTimestamp, manual: false, SyncOutcome.ConfirmationPending,
            1, 1, 0, 0, 0, Array.Empty<string>());

        Assert.EndsWith("\n\n", entry);
    }
}
