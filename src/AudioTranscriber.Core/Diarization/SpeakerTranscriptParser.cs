using System.Text.RegularExpressions;

namespace AudioTranscriber.Core.Diarization;

/// <summary>
/// Un turno parseado de una transcripción con hablantes: la etiqueta ("Persona 1", "Sin
/// identificar") tal como vino, y el texto de ese turno.
/// </summary>
public readonly record struct SpeakerBlock(string Label, string Text);

/// <summary>
/// Parsea una transcripción con hablantes de vuelta a turnos, para la vista "Leer". El formato de
/// entrada es el que produce <see cref="SpeakerTranscriptFormatter"/>:
/// <code>
/// Persona 1: bueno, esto sería el pitch…
///
/// Persona 2: ¿y cuál es el scope?
/// </code>
///
/// Es el ESPEJO EXACTO de <c>splitSpeakerBlocks</c> de la web
/// (<c>src/lib/polish/speakers.ts</c>): las dos apps muestran los mismos hablantes de la misma
/// forma, y el mismo texto tiene que parsear igual en las dos. Si el patrón o el criterio de
/// continuación cambian en una, cambian en la otra.
/// </summary>
public static partial class SpeakerTranscriptParser
{
    // Mismo patrón que la web: ancla al principio de línea/chunk, "Persona N" o "Sin identificar",
    // dos puntos y espacios/tabs opcionales. Una frase que MENCIONE "Persona 2" en el medio no abre
    // un turno nuevo.
    [GeneratedRegex(@"^(Persona \d+|Sin identificar):[ \t]*")]
    private static partial Regex BlockPattern();

    /// <summary>
    /// Parte el texto en turnos. Devuelve <c>null</c> si NO es una transcripción con hablantes (una
    /// nota normal, o una grabada en la web sin diarización): así el llamador sigue por el camino
    /// del documento plano sin inventar una estructura que no existe.
    /// </summary>
    public static IReadOnlyList<SpeakerBlock>? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        // Separa por línea(s) en blanco, igual que la web (`text.split(/\n\s*\n/)`).
        var chunks = Regex.Split(text, @"\n\s*\n");
        var blocks = new List<SpeakerBlock>();

        foreach (var raw in chunks)
        {
            var chunk = raw.Trim();
            if (chunk.Length == 0)
                continue;

            var match = BlockPattern().Match(chunk);
            if (!match.Success)
            {
                // Sin etiqueta y todavía sin ningún turno abierto → no es diarizada: que siga por el
                // camino normal.
                if (blocks.Count == 0)
                    return null;

                // Sin etiqueta pero DESPUÉS de un turno → continuación del mismo hablante. Pasa de
                // verdad: el propio pulido agrega cortes de párrafo adentro de un turno largo, y esos
                // párrafos nuevos no llevan etiqueta. Mismo criterio que la web (verificado 2026-07-16).
                var last = blocks[^1];
                blocks[^1] = last with { Text = $"{last.Text}\n\n{chunk}" };
                continue;
            }

            blocks.Add(new SpeakerBlock(match.Groups[1].Value, chunk[match.Length..].Trim()));
        }

        return blocks.Count > 0 ? blocks : null;
    }
}
