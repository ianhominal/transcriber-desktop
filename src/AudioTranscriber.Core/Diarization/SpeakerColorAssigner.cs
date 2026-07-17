namespace AudioTranscriber.Core.Diarization;

/// <summary>
/// Asigna un color estable a cada hablante para la vista "Leer". Espejo de <c>assignSpeakerColors</c>
/// de la web (<c>src/lib/transcript/speakerColor.ts</c>): la primera voz siempre lleva el primer
/// color aunque hable muchas veces, y "Sin identificar" siempre va a un neutro sin gastar un color
/// de la paleta (no es una voz más, es el descarte).
///
/// Devuelve un "slot" (índice 0..<see cref="PaletteSize"/>-1) en vez de un color concreto: la capa
/// de UI mapea slot → brush del tema (claro/oscuro). Así la lógica de asignación queda pura y
/// testeable, y el color real vive donde tiene que vivir, en los diccionarios de tema.
/// </summary>
public static class SpeakerColorAssigner
{
    /// <summary>Slot para "Sin identificar": la UI lo pinta con un neutro, no con un color de la paleta.</summary>
    public const int NeutralSlot = -1;

    /// <summary>Cantidad de colores en la paleta. Si hay más hablantes, se cicla.</summary>
    public const int PaletteSize = 6;

    /// <summary>Etiqueta que usa el diarizador para lo que no pudo atribuir (ver <see cref="SpeakerTranscriptFormatter.UnknownSpeakerLabel"/>).</summary>
    private const string Unidentified = SpeakerTranscriptFormatter.UnknownSpeakerLabel;

    /// <summary>
    /// Recibe las etiquetas en el orden en que aparecen (con repeticiones, tal como salen de
    /// <see cref="SpeakerTranscriptParser"/>) y devuelve un mapa etiqueta → slot.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Assign(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var map = new Dictionary<string, int>();
        var next = 0;
        foreach (var label in labels)
        {
            if (map.ContainsKey(label))
                continue;

            if (label == Unidentified)
            {
                map[label] = NeutralSlot;
            }
            else
            {
                map[label] = next % PaletteSize;
                next++;
            }
        }

        return map;
    }
}
