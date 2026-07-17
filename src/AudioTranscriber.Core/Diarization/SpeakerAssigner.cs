using System;
using System.Collections.Generic;
using System.Linq;
using AudioTranscriber.Core.Transcription;

namespace AudioTranscriber.Core.Diarization;

/// <summary>
/// Cruza QUÉ se dijo (segmentos de Whisper) con QUIÉN lo dijo (segmentos del diarizador).
///
/// Son dos modelos independientes que miran el mismo audio y NO están alineados entre sí: cada uno
/// corta donde le parece, así que un segmento de texto puede pisar a dos hablantes (alguien
/// interrumpe, se solapan). La regla es simple y defendible: cada segmento de texto se le atribuye
/// al hablante con MÁS SOLAPAMIENTO TEMPORAL. Es puro y determinístico a propósito — es la lógica
/// que decide si la transcripción etiquetada dice la verdad o miente, así que se testea sola, sin
/// modelos ni audio.
/// </summary>
public static class SpeakerAssigner
{
    /// <summary>
    /// Atribuye cada segmento de transcripción al hablante que más tiempo comparte con él.
    /// Si un segmento no pisa a ningún hablante (silencio, música, el diarizador no lo cubrió),
    /// queda con <c>Speaker = null</c> en vez de inventarle uno.
    /// </summary>
    public static IReadOnlyList<LabeledSegment> Assign(
        IReadOnlyList<TranscriptSegment> transcript,
        IReadOnlyList<SpeakerSegment> speakers)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(speakers);

        var result = new List<LabeledSegment>(transcript.Count);
        foreach (var seg in transcript)
        {
            result.Add(new LabeledSegment(seg.Start, seg.End, seg.Text, BestSpeaker(seg, speakers)));
        }
        return result;
    }

    /// <summary>Hablante con mayor solapamiento con <paramref name="seg"/>, o null si ninguno lo toca.</summary>
    private static int? BestSpeaker(TranscriptSegment seg, IReadOnlyList<SpeakerSegment> speakers)
    {
        int? best = null;
        var bestOverlap = TimeSpan.Zero;

        foreach (var sp in speakers)
        {
            var overlap = Overlap(seg.Start, seg.End, sp.Start, sp.End);
            // `>` estricto: ante un empate exacto gana el PRIMERO de la lista (los diarizadores la
            // devuelven ordenada por tiempo), así el resultado es estable y no depende del orden de
            // iteración de un diccionario.
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = sp.Speaker;
            }
        }

        return bestOverlap > TimeSpan.Zero ? best : null;
    }

    /// <summary>Tiempo compartido entre dos rangos (cero si no se tocan).</summary>
    private static TimeSpan Overlap(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd)
    {
        var start = aStart > bStart ? aStart : bStart;
        var end = aEnd < bEnd ? aEnd : bEnd;
        var overlap = end - start;
        return overlap > TimeSpan.Zero ? overlap : TimeSpan.Zero;
    }

    /// <summary>
    /// Cantidad de hablantes distintos realmente atribuidos. Sirve para no anunciar "2 personas"
    /// cuando en los hechos se etiquetó una sola.
    /// </summary>
    public static int DistinctSpeakers(IReadOnlyList<LabeledSegment> labeled) =>
        labeled.Where(s => s.Speaker.HasValue).Select(s => s.Speaker!.Value).Distinct().Count();
}
