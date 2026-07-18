using AudioTranscriber.Core.Workspaces;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class TranscribeGateFormatterTests
{
    /// Everything ready: Groq engine, an audio picked, nothing running.
    [Fact]
    public void Ready_HasNoReason()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: false, isRecording: false, hasSelectedAudio: true, isGroq: true, isLocalModelAvailable: false);

        Assert.Null(reason);
    }

    [Fact]
    public void LocalEngineWithModelDownloaded_HasNoReason()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: false, isRecording: false, hasSelectedAudio: true, isGroq: false, isLocalModelAvailable: true);

        Assert.Null(reason);
    }

    [Fact]
    public void NoAudioSelected_AsksForAnAudio()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: false, isRecording: false, hasSelectedAudio: false, isGroq: true, isLocalModelAvailable: true);

        Assert.Equal("Elegí un audio de la lista para transcribir.", reason);
    }

    /// THE regression: a real user had no audio selected AND no local model. The old hardcoded
    /// tooltip blamed the model, so she downloaded ~1.5 GB and the button stayed disabled.
    /// The cheap, actionable blocker must win.
    [Fact]
    public void NoAudioAndNoModel_BlamesTheAudio_NotThe1500MbDownload()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: false, isRecording: false, hasSelectedAudio: false, isGroq: false, isLocalModelAvailable: false);

        Assert.Equal("Elegí un audio de la lista para transcribir.", reason);
        Assert.DoesNotContain("modelo", reason);
    }

    [Fact]
    public void LocalEngineWithoutModel_AndAudioPicked_AsksForTheModel()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: false, isRecording: false, hasSelectedAudio: true, isGroq: false, isLocalModelAvailable: false);

        Assert.Equal("Con el motor Local hace falta descargar el modelo primero (ver arriba).", reason);
    }

    [Fact]
    public void Recording_WinsOverEverythingElse()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: true, isRecording: true, hasSelectedAudio: false, isGroq: false, isLocalModelAvailable: false);

        Assert.Equal("Terminá la grabación antes de transcribir.", reason);
    }

    [Fact]
    public void Busy_ReportsBusy()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: true, isRecording: false, hasSelectedAudio: true, isGroq: true, isLocalModelAvailable: true);

        Assert.Equal("Esperá a que termine lo que está corriendo.", reason);
    }

    // ---- FEATURE 2 (2026-07-17): batch-transcribir un proyecto sin audio puntual seleccionado ----

    [Fact]
    public void ProjectWithPendingAudios_NoSingleAudioSelected_HasNoReason()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: false, isRecording: false, hasSelectedAudio: false, isGroq: true, isLocalModelAvailable: false,
            hasPendingProjectAudios: true);

        Assert.Null(reason);
    }

    [Fact]
    public void ProjectWithPendingAudios_LocalEngineWithoutModel_AsksForTheModel()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: false, isRecording: false, hasSelectedAudio: false, isGroq: false, isLocalModelAvailable: false,
            hasPendingProjectAudios: true);

        Assert.Equal("Con el motor Local hace falta descargar el modelo primero (ver arriba).", reason);
    }

    /// Sin el nuevo parámetro (default false), el comportamiento viejo no cambia un carácter:
    /// mismo mensaje que NoAudioSelected_AsksForAnAudio de arriba.
    [Fact]
    public void NoAudioAndNoPendingProjectAudios_StillAsksForAnAudio()
    {
        var reason = TranscribeGateFormatter.DisabledReason(
            isBusy: false, isRecording: false, hasSelectedAudio: false, isGroq: true, isLocalModelAvailable: true,
            hasPendingProjectAudios: false);

        Assert.Equal("Elegí un audio de la lista para transcribir.", reason);
    }
}
