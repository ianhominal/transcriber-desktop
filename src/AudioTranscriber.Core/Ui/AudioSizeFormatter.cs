using System.Globalization;

namespace AudioTranscriber.Core.Ui;

/// <summary>
/// Peso legible de un audio para la lista de notas.
///
/// Existe por un caso real reportado desde un proyecto COMPARTIDO: las notas que llegaron por sync
/// sin su archivo de audio en disco se mostraban como "0 KB". Eso es información falsa — el audio no
/// pesa cero, simplemente no está en esta PC (puede estar en la nube, o la nota puede ser solo
/// texto). Antes esto vivía inline en <c>AudioItemVm.SizeText</c>, donde no se podía testear sin
/// WPF; el caso "no hay archivo local" no estaba contemplado en ninguna parte.
/// </summary>
public static class AudioSizeFormatter
{
    /// <summary>
    /// Texto de "sin archivo local". No dice "sin audio" a secas a propósito: para una nota
    /// compartida el audio puede existir perfectamente en la nube, solo no está bajado acá.
    /// </summary>
    public const string NoLocalAudioText = "Sin audio local";

    /// <summary>
    /// Peso legible: KB por debajo de 1 MiB, MB desde ahí. Cuando <paramref name="hasAudio"/> es
    /// false NO se muestra ningún número — se devuelve <see cref="NoLocalAudioText"/>, sin mirar
    /// <paramref name="sizeBytes"/>.
    ///
    /// El número se formatea con cultura es-AR EXPLÍCITA (coma decimal), no con la del sistema:
    /// mismo criterio ya documentado en <see cref="Transcription.EngineSelector.FormatSize"/> — el
    /// texto que rodea a este número está en español, así que un Windows en inglés no debería
    /// meterle un punto decimal en el medio.
    /// </summary>
    public static string Format(long sizeBytes, bool hasAudio)
    {
        if (!hasAudio) return NoLocalAudioText;

        var es = CultureInfo.GetCultureInfo("es-AR");
        const long mb = 1024 * 1024;
        return sizeBytes >= mb
            ? string.Format(es, "{0:0.0} MB", sizeBytes / (double)mb)
            : string.Format(es, "{0:0} KB", sizeBytes / 1024.0);
    }
}
