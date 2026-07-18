namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Why the "Transcribir" button is disabled, in the user's words.
///
/// Bugfix 2026-07-15 (reported by a real user): the tooltip used to be a hardcoded string that
/// always said "Con el motor Local hace falta descargar el modelo primero", while the button
/// actually gates on FOUR conditions. A user with no audio selected read that tooltip, downloaded
/// the ~1.5 GB local model for nothing, and the button stayed disabled. A disabled control must
/// state the reason it is ACTUALLY disabled, not one of the reasons it could be.
///
/// Order matters: it reports the blocker the user can act on FIRST (pick an audio) before the
/// expensive one (download a model), so nobody is sent on a 1.5 GB detour they didn't need.
/// </summary>
public static class TranscribeGateFormatter
{
    /// <summary>
    /// Reason the button is disabled, or <c>null</c> when it should be enabled.
    ///
    /// <paramref name="hasPendingProjectAudios"/> (FEATURE 2, 2026-07-17): true when no single
    /// audio is selected, but a PROJECT is, and that project has at least one audio still without
    /// a transcript — "Transcribir" batch-transcribes the whole project in that case (see
    /// MainViewModel.TranscribeProjectAsync), so the old "pick an audio" blocker no longer applies.
    /// Defaults to false so every existing caller (single-audio flow) keeps its exact old behavior.
    /// </summary>
    public static string? DisabledReason(
        bool isBusy,
        bool isRecording,
        bool hasSelectedAudio,
        bool isGroq,
        bool isLocalModelAvailable,
        bool hasPendingProjectAudios = false)
    {
        if (isRecording)
            return "Terminá la grabación antes de transcribir.";

        if (isBusy)
            return "Esperá a que termine lo que está corriendo.";

        if (!hasSelectedAudio && !hasPendingProjectAudios)
            return "Elegí un audio de la lista para transcribir.";

        if (!isGroq && !isLocalModelAvailable)
            return "Con el motor Local hace falta descargar el modelo primero (ver arriba).";

        return null;
    }
}
