namespace AudioTranscriber.Core.Transcription;

/// <summary>
/// Reglas de "¿qué pasó con el pulido?" a partir de lo que devuelve <c>POST /api/polish</c>
/// (<c>polishedChunks</c> / <c>totalChunks</c>). Puro y testeable: son decisiones, no UI.
///
/// El servidor puede partir un texto largo en varios tramos y, si alguno falla o se descarta (ver
/// la guarda anti-invención del backend), conserva el original de ESE tramo. Distinguir "salió
/// todo", "salió a medias" y "no había nada que hacer" es lo que decide si el botón queda apagado
/// ("Texto mejorado ✓") o habilitado para reintentar.
/// </summary>
public static class PolishState
{
    /// <summary>
    /// No había NADA que mejorar: todos los tramos eran demasiado cortos como para mandárselos al
    /// modelo (una charla con turnos de dos palabras, por ejemplo). No es un fallo.
    /// </summary>
    public static bool NothingToDo(int totalChunks) => totalChunks == 0;

    /// <summary>Quedó algún tramo sin pulir (falló o se descartó): reintentar tiene sentido.</summary>
    public static bool IsPartial(int polishedChunks, int totalChunks) =>
        !NothingToDo(totalChunks) && polishedChunks < totalChunks;

    /// <summary>
    /// El texto se puede dar por mejorado (botón apagado). Solo cuando NO quedó nada pendiente:
    /// un resultado parcial deja el botón habilitado a propósito.
    /// </summary>
    public static bool ShouldMarkPolished(int polishedChunks, int totalChunks) =>
        !IsPartial(polishedChunks, totalChunks);
}
