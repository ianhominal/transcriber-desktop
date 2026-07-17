using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Convierte cualquier audio soportado (mp3/wav/ogg/opus/mp4/m4a/webm) al formato que exige
/// Whisper: WAV PCM de 16 kHz, mono, 16 bits. En C# puro (sin FFmpeg externo):
/// NAudio para mp3/wav, Media Foundation para mp4/m4a/aac/webm, y Concentus para OGG/Opus
/// (notas de voz de WhatsApp).
/// </summary>
public sealed class AudioConverter
{
    public const int TargetSampleRate = 16000;

    /// <summary>
    /// Lee <paramref name="inputPath"/> y escribe un WAV 16 kHz mono 16-bit en
    /// <paramref name="outputPath"/>.
    /// </summary>
    public void ToWhisperWav(string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("No se encontró el audio de entrada.", inputPath);

        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        if (ext is ".ogg" or ".opus")
            ConvertOpusOgg(inputPath, outputPath, ct);
        else if (ext is ".mp4" or ".m4a" or ".aac" or ".webm")
            ConvertWithMediaFoundation(inputPath, outputPath);
        else
            ConvertWithNAudio(inputPath, outputPath);
    }

    /// <summary>
    /// Camino para mp4/m4a/aac/webm: extrae el audio con Media Foundation (nativo de Windows).
    /// Para un mp4 con video, toma la pista de audio. <c>.webm</c> (Opus, ver
    /// Workspace.SupportedExtensions) también se decodifica acá: Media Foundation lo abre
    /// directamente en este entorno, sin necesitar un demuxer WebM/Matroska propio.
    /// </summary>
    private static void ConvertWithMediaFoundation(string inputPath, string outputPath)
    {
        using var reader = new MediaFoundationReader(inputPath);
        ISampleProvider mono = ToMono(reader.ToSampleProvider());
        WriteResampled16(mono, outputPath);
    }

    /// <summary>Camino para mp3/wav (formatos que NAudio decodifica de forma nativa).</summary>
    private static void ConvertWithNAudio(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);

        ISampleProvider mono = ToMono(reader);
        WriteResampled16(mono, outputPath);
    }

    /// <summary>
    /// Camino para OGG/Opus. NAudio no decodifica Opus, así que usamos Concentus:
    /// decodificamos a PCM 48 kHz mono y lo pasamos por el mismo resampleo a 16 kHz.
    /// </summary>
    private static void ConvertOpusOgg(string inputPath, string outputPath, CancellationToken ct)
    {
        const int decodeRate = 48000; // Opus captura/decodifica de forma natural a 48 kHz
        var decoder = OpusCodecFactory.CreateDecoder(decodeRate, 1);

        using var fileIn = File.OpenRead(inputPath);
        var oggIn = new OpusOggReadStream(decoder, fileIn);

        var pcm = new List<short>();
        while (oggIn.HasNextPacket)
        {
            ct.ThrowIfCancellationRequested(); // cancelación cooperativa real
            short[] packet = oggIn.DecodeNextPacket();
            if (packet is { Length: > 0 })
                pcm.AddRange(packet);
        }

        if (pcm.Count == 0)
            throw new InvalidOperationException("No se pudo decodificar audio del archivo OGG/Opus.");

        // shorts (PCM 16-bit, 48 kHz mono) -> bytes
        var shortArr = pcm.ToArray();
        var bytes = new byte[shortArr.Length * sizeof(short)];
        Buffer.BlockCopy(shortArr, 0, bytes, 0, bytes.Length);

        using var raw = new RawSourceWaveStream(
            new MemoryStream(bytes), new WaveFormat(decodeRate, 16, 1));
        WriteResampled16(raw.ToSampleProvider(), outputPath);
    }

    /// <summary>Mezcla a mono si hace falta (soporta mono o estéreo).</summary>
    private static ISampleProvider ToMono(ISampleProvider source) => source.WaveFormat.Channels switch
    {
        1 => source,
        2 => new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f },
        _ => throw new NotSupportedException(
            $"El audio tiene {source.WaveFormat.Channels} canales; solo se soportan mono o estéreo."),
    };

    /// <summary>Resamplea a 16 kHz (si hace falta) y escribe WAV PCM 16-bit.</summary>
    private static void WriteResampled16(ISampleProvider source, string outputPath)
    {
        ISampleProvider resampled = source.WaveFormat.SampleRate == TargetSampleRate
            ? source
            : new WdlResamplingSampleProvider(source, TargetSampleRate);

        WaveFileWriter.CreateWaveFile16(outputPath, resampled);
    }
}
