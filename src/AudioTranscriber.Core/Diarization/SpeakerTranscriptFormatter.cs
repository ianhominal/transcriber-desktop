using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AudioTranscriber.Core.Diarization;

/// <summary>
/// Arma el texto final con los hablantes marcados.
///
/// Junta los segmentos consecutivos del MISMO hablante en un solo bloque: el diarizador corta cada
/// pocos segundos, y una línea "Persona 1:" por cada frasecita es ilegible — justo lo contrario de
/// lo que se pidió ("junta preguntas, devoluciones y respuestas en un único bloque").
///
/// Los nombres son "Persona 1", "Persona 2"… a propósito: el diarizador agrupa voces, no sabe
/// quién es quién. Prometer un nombre real sería mentir.
/// </summary>
public static class SpeakerTranscriptFormatter
{
    /// <summary>Etiqueta para un segmento sin hablante atribuido (silencio, música, ruido).</summary>
    public const string UnknownSpeakerLabel = "Sin identificar";

    /// <summary>Nombre visible de un hablante. Los ids del diarizador arrancan en 0; la gente cuenta desde 1.</summary>
    public static string SpeakerLabel(int? speaker) =>
        speaker.HasValue ? $"Persona {speaker.Value + 1}" : UnknownSpeakerLabel;

    /// <summary>
    /// Renumera los hablantes para que queden CONSECUTIVOS y en orden de aparición.
    ///
    /// Bugfix 2026-07-15 (reportado con un caso real): el diarizador devuelve ids de cluster
    /// arbitrarios y no todos terminan con texto atribuido. Con los clusters 0 y 2 sobrevivientes,
    /// la pantalla mostraba "Persona 1" y "Persona 3" — y el usuario se queda buscando a la
    /// "Persona 2" que nunca existió. Los ids del diarizador son un detalle interno: lo que la
    /// usuaria tiene que ver es "hay dos personas, esta y esta".
    /// </summary>
    public static IReadOnlyList<LabeledSegment> RenumberSpeakers(IReadOnlyList<LabeledSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var map = new Dictionary<int, int>();
        var result = new List<LabeledSegment>(segments.Count);

        foreach (var seg in segments)
        {
            int? renumbered = null;
            if (seg.Speaker.HasValue)
            {
                if (!map.TryGetValue(seg.Speaker.Value, out var compact))
                {
                    compact = map.Count;
                    map[seg.Speaker.Value] = compact;
                }
                renumbered = compact;
            }
            result.Add(seg with { Speaker = renumbered });
        }

        return result;
    }

    /// <summary>
    /// Texto con los turnos marcados, del estilo:
    /// <code>
    /// Persona 1: bueno, esto sería el pitch de Cadejos…
    ///
    /// Persona 2: ¿y cuál es el scope del vertical slice?
    /// </code>
    /// </summary>
    /// <summary>
    /// True si el texto es SOLO una marca de no-habla de Whisper ([MÚSICA], [SILENCIO], [Music]…).
    /// Nadie las "dijo": son anotaciones del modelo. Atribuírselas a alguien inventa una persona
    /// (caso real: un tramo de música quedó como "Persona 3: [MÚSICA]", y esa persona no existía).
    /// </summary>
    public static bool IsNonSpeechMarker(string text)
    {
        var t = text.Trim();
        if (t.Length < 2)
            return false;

        var isBracketed = (t[0] == '[' && t[^1] == ']') || (t[0] == '(' && t[^1] == ')');
        if (!isBracketed)
            return false;

        // Solo la marca sola: "[MÚSICA]" sí, "[MÚSICA] y ahí va" no (eso tiene habla real adentro).
        return !t.AsSpan(1, t.Length - 2).ContainsAny(']', ')');
    }

    public static string Format(IReadOnlyList<LabeledSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        // Las marcas de no-habla no se le atribuyen a nadie ANTES de renumerar, así no consumen un
        // número de persona; y recién ahí los ids del diarizador se compactan a 1, 2, 3…
        var cleaned = segments
            .Select(s => IsNonSpeechMarker(s.Text) ? s with { Speaker = null } : s)
            .ToList();
        segments = RenumberSpeakers(cleaned);

        var sb = new StringBuilder();
        int? currentSpeaker = null;
        var hasBlock = false;
        var block = new StringBuilder();

        foreach (var seg in segments)
        {
            var text = seg.Text.Trim();
            if (text.Length == 0)
                continue;

            if (!hasBlock || seg.Speaker != currentSpeaker)
            {
                if (hasBlock)
                {
                    AppendBlock(sb, currentSpeaker, block);
                    block.Clear();
                }
                currentSpeaker = seg.Speaker;
                hasBlock = true;
            }

            if (block.Length > 0)
                block.Append(' ');
            block.Append(text);
        }

        if (hasBlock && block.Length > 0)
            AppendBlock(sb, currentSpeaker, block);

        return sb.ToString().TrimEnd();
    }

    private static void AppendBlock(StringBuilder sb, int? speaker, StringBuilder block)
    {
        if (block.Length == 0)
            return;
        if (sb.Length > 0)
            sb.Append("\n\n");
        sb.Append(SpeakerLabel(speaker)).Append(": ").Append(block);
    }
}
