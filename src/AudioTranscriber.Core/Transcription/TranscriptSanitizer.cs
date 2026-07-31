using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioTranscriber.Core.Transcription;

/// <summary>
/// Limpia el texto que devuelve Whisper antes de guardarlo. Hoy resuelve un solo problema, pero el
/// más visible: los loops de alucinación (ver <see cref="RepetitionLoopDetector"/> para el contexto
/// completo del caso real).
///
/// Aunque el streaming se corte al detectar el loop, lo que se alcanzó a acumular puede traer la
/// frase repetida decenas de veces -- y además quedan transcripciones viejas ya guardadas así.
/// Lógica pura, sin I/O.
/// </summary>
public static class TranscriptSanitizer
{
    /// <summary>
    /// Repeticiones consecutivas de la MISMA oración a partir de las cuales se colapsan a una sola.
    /// Tres es suficiente para no tocar una muletilla real ("Sí. Sí.") y sí barrer un loop.
    /// </summary>
    private const int RepeticionesParaColapsar = 3;

    // Corta en oraciones conservando el signo de puntuación final; el último tramo puede no tenerlo.
    private static readonly Regex Oraciones = new(@"[^.!?]*[.!?]+|[^.!?]+", RegexOptions.Compiled);

    /// <summary>
    /// Forma canónica para comparar dos fragmentos: sin mayúsculas, sin puntuación y con los
    /// espacios colapsados. Así "Mae'r cydnabod yn dda iawn." y "  mae'r CYDNABOD yn dda iawn  "
    /// cuentan como el mismo texto.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        var espacioPendiente = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                espacioPendiente = sb.Length > 0;
                continue;
            }

            // La puntuación se descarta, pero los apóstrofos y guiones internos son parte de la
            // palabra en varios idiomas (galés, catalán, francés) -- sacarlos junta palabras que no
            // deberían juntarse.
            if (char.IsPunctuation(c) && c is not '\'' and not '-')
                continue;

            if (espacioPendiente)
            {
                sb.Append(' ');
                espacioPendiente = false;
            }

            sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Colapsa toda tanda de <see cref="RepeticionesParaColapsar"/> o más oraciones consecutivas
    /// iguales a una sola aparición (se conserva la forma de la primera). Lo que no es un loop
    /// vuelve tal cual.
    /// </summary>
    public static string? CollapseRepeatedSentences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var oraciones = SplitSentences(text);
        if (oraciones.Count == 0)
            return text;

        var resultado = new List<string>(oraciones.Count);
        var i = 0;
        while (i < oraciones.Count)
        {
            var actual = oraciones[i];
            var clave = Normalize(actual);

            var j = i + 1;
            while (j < oraciones.Count && Normalize(oraciones[j]) == clave)
                j++;

            var repeticiones = j - i;
            // Bajo el umbral se copian todas: una repetición corta puede ser habla real.
            var aCopiar = repeticiones >= RepeticionesParaColapsar ? 1 : repeticiones;
            for (var k = 0; k < aCopiar; k++)
                resultado.Add(oraciones[i + k]);

            i = j;
        }

        return string.Join(" ", resultado);
    }

    /// <summary>
    /// True cuando el texto es, en su mayor parte, la misma oración repetida -- señal de que la
    /// transcripción no sirve y hay que avisarle a la persona en vez de guardarla como si nada.
    /// </summary>
    public static bool LooksLikeHallucinationLoop(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var oraciones = SplitSentences(text);
        if (oraciones.Count < RepeticionesParaColapsar)
            return false;

        var rachaMaxima = 1;
        var racha = 1;
        for (var i = 1; i < oraciones.Count; i++)
        {
            if (Normalize(oraciones[i]) == Normalize(oraciones[i - 1]))
            {
                racha++;
                rachaMaxima = Math.Max(rachaMaxima, racha);
            }
            else
            {
                racha = 1;
            }
        }

        // El mismo umbral que usa el detector en streaming: coherencia entre las dos puertas.
        return rachaMaxima >= RepetitionLoopDetector.UmbralPorDefecto;
    }

    private static List<string> SplitSentences(string text)
    {
        var oraciones = new List<string>();
        foreach (Match m in Oraciones.Matches(text))
        {
            var trozo = m.Value.Trim();
            if (trozo.Length > 0)
                oraciones.Add(trozo);
        }
        return oraciones;
    }
}
