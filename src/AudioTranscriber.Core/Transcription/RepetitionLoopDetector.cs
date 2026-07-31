namespace AudioTranscriber.Core.Transcription;

/// <summary>
/// Detecta, mientras la transcripción va llegando en streaming, que Whisper entró en un loop de
/// alucinación: el mismo segmento repetido una y otra vez.
///
/// Por qué existe: Whisper alimenta el texto del segmento anterior como prompt del siguiente
/// (<c>condition_on_previous_text</c>). Cuando el audio no tiene habla clara -- una grabación de
/// pantalla casi muda, por ejemplo -- el modelo inventa una frase, esa frase entra como contexto
/// del segmento que sigue, y se muerde la cola. Un caso real terminó con "Mae'r cydnabod yn dda
/// iawn." (galés) repetido cientos de veces sobre un mp4 de escritorio, y la persona tuvo que
/// esperar a que terminara igual, viendo cómo la app "mandaba fruta".
///
/// <see cref="TranscriptionService"/> ya desactiva el contexto entre segmentos, que es el arreglo
/// de fondo. Esto es la red: si el loop aparece igual, se corta el streaming en vez de gastarle a
/// la persona diez minutos de CPU para devolverle basura.
///
/// Lógica pura (tiene estado, pero ni I/O ni dependencias): testeable de punta a punta.
/// </summary>
public sealed class RepetitionLoopDetector
{
    /// <summary>
    /// Repeticiones consecutivas idénticas que se toleran antes de cantar loop. Alto a propósito:
    /// en habla real nadie repite la misma oración exacta 12 veces seguidas, pero sí puede repetir
    /// una muletilla ("Sí. Sí. Sí.") unas pocas.
    /// </summary>
    public const int UmbralPorDefecto = 12;

    private readonly int _umbral;
    private string? _ultimoNormalizado;
    private int _repeticiones;

    public RepetitionLoopDetector(int umbral = UmbralPorDefecto)
    {
        if (umbral < 2)
            throw new ArgumentOutOfRangeException(nameof(umbral), umbral, "El umbral tiene que ser 2 o más.");
        _umbral = umbral;
    }

    /// <summary>True una vez que se detectó el loop. No vuelve a false.</summary>
    public bool Detectado { get; private set; }

    /// <summary>
    /// Registra un segmento recién emitido y devuelve <see cref="Detectado"/>. Los segmentos vacíos
    /// o de solo espacios se ignoran: los silencios los emiten de a montones y no son alucinaciones.
    /// </summary>
    public bool Observe(string? segmentText)
    {
        if (Detectado)
            return true;

        var normalizado = TranscriptSanitizer.Normalize(segmentText);
        if (normalizado.Length == 0)
            return false;

        if (normalizado == _ultimoNormalizado)
        {
            _repeticiones++;
            if (_repeticiones >= _umbral)
                Detectado = true;
        }
        else
        {
            _ultimoNormalizado = normalizado;
            _repeticiones = 1;
        }

        return Detectado;
    }
}
