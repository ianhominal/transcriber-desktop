namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Genera nombres de archivo para grabaciones del micrófono, con formato determinístico y sin
/// caracteres inválidos para el sistema de archivos de Windows. Lógica pura: recibe la
/// fecha/hora, no lee el reloj.
/// </summary>
public static class RecordingFileNamer
{
    private const string DateFormat = "yyyy-MM-dd HH-mm-ss";

    /// <summary>Nombre para una grabación manual del micrófono (botón Grabar/Detener).</summary>
    public static string Generate(DateTime timestamp) =>
        $"Grabación {timestamp.ToString(DateFormat)}.wav";

    /// <summary>Nombre para una grabación de reunión (micrófono + audio del sistema, botón "Grabar
    /// reunión"): mismo formato de fecha que <see cref="Generate"/>, prefijo distinto para
    /// distinguirlas a simple vista en la lista de audios.</summary>
    public static string GenerateForMeeting(DateTime timestamp) =>
        $"Reunión {timestamp.ToString(DateFormat)}.wav";
}
