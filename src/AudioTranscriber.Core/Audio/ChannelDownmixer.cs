using System;

namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Baja audio intercalado de cualquier cantidad de canales a mono, promediando. Lógica pura (sin
/// NAudio, sin tocar ningún dispositivo): existe porque el audio del sistema (lo que suena en la
/// PC, capturado por <see cref="MeetingRecorder"/>) casi siempre llega estéreo -- y a veces con más
/// canales, en placas con salidas 5.1/7.1 -- y hay que reducirlo a un solo canal antes de mezclarlo
/// con el micrófono (mono) vía <see cref="AudioMixer"/>.
///
/// Promedia en vez de quedarse con un solo canal: así ninguna voz desaparece si, por ejemplo,
/// alguien de la reunión suena solo por el canal derecho.
/// </summary>
public static class ChannelDownmixer
{
    /// <summary>
    /// Convierte <paramref name="interleaved"/> (frames de <paramref name="channels"/> canales,
    /// intercalados: <c>L0 R0 L1 R1 …</c> para estéreo) a mono en <paramref name="destination"/>.
    ///
    /// Con un solo canal copia directo (no hay nada que promediar). Devuelve la cantidad de frames
    /// (muestras mono) realmente escritos: el mínimo entre los frames completos que entran en
    /// <paramref name="interleaved"/> (una muestra final incompleta se descarta, nunca se lee fuera
    /// de rango) y el espacio disponible en <paramref name="destination"/>.
    /// </summary>
    public static int ToMono(ReadOnlySpan<float> interleaved, int channels, Span<float> destination)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "La cantidad de canales tiene que ser mayor a cero.");

        int frames = Math.Min(interleaved.Length / channels, destination.Length);

        if (channels == 1)
        {
            interleaved[..frames].CopyTo(destination);
            return frames;
        }

        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0f;
            int baseIndex = frame * channels;
            for (int ch = 0; ch < channels; ch++)
                sum += interleaved[baseIndex + ch];
            destination[frame] = sum / channels;
        }
        return frames;
    }
}
