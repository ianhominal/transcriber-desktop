namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Calcula el nivel de audio (para el VU meter) a partir de un buffer PCM 16-bit. Lógica pura:
/// no lee del micrófono, solo procesa bytes ya capturados.
/// </summary>
public static class AudioLevelMeter
{
    /// <summary>
    /// RMS normalizado (0.0–1.0) de un buffer PCM 16-bit (mono o intercalado). Si el buffer
    /// tiene un byte suelto al final (longitud impar) se ignora, nunca se lee fuera de rango.
    /// </summary>
    public static double CalculateRms(byte[] pcm16)
    {
        if (pcm16 is null || pcm16.Length < 2)
            return 0.0;

        int sampleCount = pcm16.Length / 2;
        double sumSquares = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            double normalized = sample / (double)short.MaxValue;
            sumSquares += normalized * normalized;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        return Math.Clamp(rms, 0.0, 1.0);
    }
}
