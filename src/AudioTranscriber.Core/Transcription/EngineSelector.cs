using System.Globalization;

namespace AudioTranscriber.Core.Transcription;

/// <summary>What to do when the picked engine can't handle the file.</summary>
public enum EngineDecision
{
    /// <summary>The current engine can do it: don't touch anything.</summary>
    Keep,

    /// <summary>Too big for the cloud, and the local model is ready: switch and say so.</summary>
    SwitchToLocal,

    /// <summary>Too big for the cloud, and the local model isn't downloaded yet: can't run anywhere.</summary>
    NeedsLocalModel,
}

/// <summary>
/// Picks the engine that can actually transcribe a given file, BEFORE trying.
///
/// The cloud path caps at 25 MB (the backend answers 413 past that — see /api/transcribe), while
/// the local engine has no size cap: it runs Whisper on the user's own machine. Without this, a
/// large file was uploaded in full just to fail at the end — a real user brought a 1.6 GB video,
/// 65x over the cloud cap, which would have meant a very long upload for a guaranteed error.
///
/// Switching is only proposed when the local model is ALREADY downloaded: sending someone to a
/// ~1.5 GB download as an implicit side effect of pressing "Transcribir" would trade one bad
/// surprise for another. And it is never silent — the user picked an engine on purpose.
/// </summary>
public static class EngineSelector
{
    /// <summary>Cloud cap, mirrors the backend's own limit in /api/transcribe.</summary>
    public const long CloudMaxBytes = 25L * 1024 * 1024;

    public static EngineDecision Decide(long fileSizeBytes, bool isCloudEngine, bool isLocalModelAvailable)
    {
        if (!isCloudEngine || fileSizeBytes <= CloudMaxBytes)
            return EngineDecision.Keep;

        return isLocalModelAvailable ? EngineDecision.SwitchToLocal : EngineDecision.NeedsLocalModel;
    }

    /// <summary>Message for the user, or <c>null</c> when nothing changed.</summary>
    public static string? Notice(EngineDecision decision, long fileSizeBytes) => decision switch
    {
        EngineDecision.SwitchToLocal =>
            $"Este audio pesa {FormatSize(fileSizeBytes)} y en la nube el máximo es 25 MB, así que lo transcribimos en tu PC (motor Local). Puede tardar más.",
        EngineDecision.NeedsLocalModel =>
            $"Este audio pesa {FormatSize(fileSizeBytes)} y en la nube el máximo es 25 MB. Para transcribirlo en tu PC descargá el modelo local (arriba); se baja una sola vez.",
        _ => null,
    };

    /// <summary>
    /// Human size, so nobody has to read bytes.
    ///
    /// Formatted with an explicit Spanish culture rather than the machine's: the UI copy around
    /// this number is Spanish, so a decimal comma is what belongs next to it. Left to the ambient
    /// culture, a Windows set to English would render "1.6 GB" inside an otherwise Spanish
    /// sentence.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        var es = CultureInfo.GetCultureInfo("es-AR");
        const long mb = 1024 * 1024;
        if (bytes >= 1024 * mb)
            return string.Format(es, "{0:0.#} GB", bytes / (double)(1024 * mb));
        return string.Format(es, "{0:0.#} MB", bytes / (double)mb);
    }
}
