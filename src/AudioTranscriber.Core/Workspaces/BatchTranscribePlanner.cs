namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Estado mínimo de un audio del árbol necesario para decidir si "Transcribir" en lote (con un
/// PROYECTO seleccionado, sin audio puntual — ver FEATURE 2, brief 1.0.52) debe incluirlo.
/// Deliberadamente desacoplado de <c>AudioItemVm</c> (App, WPF) y de <see cref="AudioItem"/> (cuyo
/// <c>HasTranscript</c> es un chequeo de disco vía <c>File.Exists</c>): el ViewModel arma esto
/// desde el estado YA CARGADO en memoria de cada AudioItemVm, sin tocar disco de nuevo.
/// </summary>
public readonly record struct BatchTranscribeAudioStatus(bool HasAudio, bool HasTranscript);

/// <summary>
/// Lógica pura de "transcribir todos los audios sin transcribir de un proyecto, uno por uno" (ver
/// <c>MainViewModel.TranscribeProjectAsync</c>): qué audios entran en el lote, y los mensajes de
/// confirmación/progreso/resumen. La orquestación async (loopear awaits, respetar cancelación)
/// vive en el ViewModel — acá solo lo determinístico y testeable sin UI ni disco.
/// </summary>
public static class BatchTranscribePlanner
{
    /// <summary>
    /// True si un audio hace falta transcribir en el lote: tiene audio real (no es una
    /// transcripción solo-texto, ver <see cref="AudioItem.HasAudio"/>) y todavía no tiene
    /// transcript.
    /// </summary>
    public static bool IsPending(BatchTranscribeAudioStatus status) => status.HasAudio && !status.HasTranscript;

    /// <summary>Cuántos audios de <paramref name="statuses"/> hace falta transcribir.</summary>
    public static int CountPending(IEnumerable<BatchTranscribeAudioStatus> statuses) =>
        statuses.Count(IsPending);

    /// <summary>Mensaje de confirmación antes de arrancar el lote.</summary>
    public static string ConfirmMessage(string projectTitle, int pendingCount) =>
        $"¿Transcribir los {pendingCount} audio(s) sin transcribir de '{projectTitle}'?";

    /// <summary>Mensaje de progreso mientras corre el lote ("Transcribiendo N de M…").</summary>
    public static string ProgressMessage(int current, int total) =>
        $"Transcribiendo {current} de {total}…";

    /// <summary>
    /// Resumen final del lote. Sin errores, confirma cuántos se transcribieron; con errores,
    /// desglosa éxitos/fallos para que la usuaria sepa cuáles quedaron pendientes (ver brief:
    /// "si un audio falla, seguí con el resto y avisá al final cuántos salieron").
    /// </summary>
    public static string SummaryMessage(int total, int failed)
    {
        if (failed <= 0)
            return $"Listo: se transcribieron {total} audio(s).";

        var succeeded = total - failed;
        return $"Listo: se transcribieron {succeeded} de {total} audio(s) ({failed} con error).";
    }

    /// <summary>
    /// Mensaje cuando el lote se corta a mitad de camino porque se pidió cancelar (ver
    /// <c>MainViewModel.Cancel</c>). <paramref name="attempted"/> es cuántos se llegaron a
    /// intentar (con éxito o no) antes de cortar.
    /// </summary>
    public static string CancelledMessage(int attempted, int total, int failed)
    {
        var succeeded = attempted - failed;
        return $"Transcripción del proyecto cancelada. Se transcribieron {succeeded} de {total} audio(s).";
    }
}
