using System;

namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Mezcla dos fuentes de audio mono (tu micrófono + lo que suena en la PC) en una sola pista.
///
/// Existe por un caso real: para transcribir una reunión hacen falta LAS DOS puntas — el sistema
/// solo te da a los demás, el micrófono solo te da a vos. Grabar una sola deja la reunión a medias.
///
/// Las dos trampas que este diseño resuelve, y por qué el largo lo manda el llamador (el reloj de
/// pared) y no los buffers:
///
/// 1. DERIVA DE RELOJES: el micrófono y la placa de sonido son dispositivos distintos, cada uno con
///    su propio cristal. En una reunión de una hora y media uno entrega miles de muestras más que el
///    otro. Si se mezclara "lo que haya en cada buffer", las dos voces se irían corriendo entre sí.
///
/// 2. SILENCIOS: WASAPI loopback NO entrega nada mientras no suena audio. Si se concatenara solo lo
///    que llega, los silencios se borrarían y el audio se COMPRIMIRÍA — la reunión quedaría más
///    corta que la realidad y desfasada del micrófono.
///
/// Por eso <see cref="MixInto"/> escribe SIEMPRE <c>destination.Length</c> muestras: lo que una
/// fuente no tenga se rellena con silencio. El destino es el metrónomo; las fuentes se acomodan.
/// </summary>
public static class AudioMixer
{
    /// <summary>
    /// Mezcla <paramref name="a"/> y <paramref name="b"/> en <paramref name="destination"/>.
    ///
    /// Llena TODO el destino: donde una fuente se quedó sin muestras, aporta silencio (0). Eso
    /// mantiene la línea de tiempo aunque una de las dos venga atrasada o no haya entregado nada.
    /// </summary>
    public static void MixInto(Span<float> destination, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            var left = i < a.Length ? a[i] : 0f;
            var right = i < b.Length ? b[i] : 0f;
            destination[i] = Clamp(left + right);
        }
    }

    /// <summary>
    /// Suma dos muestras acotando el resultado a [-1, 1].
    ///
    /// Sumar dos fuentes puede pasarse de rango (dos personas fuerte a la vez). Sin acotar, el valor
    /// "da la vuelta" al convertir a PCM de 16 bits y se escucha como un chasquido. Acotar distorsiona
    /// apenas en los picos, que para voz es intercambiable — y es MUCHO mejor que el chasquido.
    /// No se atenúa cada fuente a la mitad a propósito: bajaría el volumen de toda la reunión para
    /// prevenir un pico ocasional, y un audio bajo transcribe peor.
    /// </summary>
    public static float Clamp(float sample)
    {
        // Lo NO finito se descarta PRIMERO: NaN/±Infinity no son "un sonido muy fuerte", son un
        // valor roto. Acotarlos a ±1 (que es lo que pasaba antes de este orden) metía un chasquido
        // a todo volumen en el archivo; silencio es la lectura segura de un dato basura.
        if (!float.IsFinite(sample)) return 0f;

        if (sample > 1f) return 1f;
        if (sample < -1f) return -1f;
        return sample;
    }

    /// <summary>
    /// Cuántas muestras corresponden a <paramref name="elapsed"/> a <paramref name="sampleRate"/> Hz,
    /// descontando las <paramref name="alreadyWritten"/> que ya se escribieron.
    ///
    /// Este es el metrónomo: cuánto audio DEBERÍA existir a esta altura según el reloj, sin importar
    /// cuánto entregó cada dispositivo. Nunca negativo (si el reloj se atrasa, no se borra nada ya
    /// escrito: se espera).
    /// </summary>
    public static int SamplesOwed(TimeSpan elapsed, int sampleRate, long alreadyWritten)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var expected = (long)(elapsed.TotalSeconds * sampleRate);
        var owed = expected - alreadyWritten;
        return owed > 0 ? (int)owed : 0;
    }
}
