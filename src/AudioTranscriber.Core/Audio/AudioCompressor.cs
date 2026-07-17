using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.Wave;

namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Comprime un audio a Opus/Ogg mono para SUBIRLO a la nube. Motivo: el desktop graba en WAV sin
/// comprimir (16 kHz mono = ~1,8 MB/min), y la subida al backend tiene un tope de ~4,5 MB, así que
/// entraban ~2,5 min de audio y las reuniones daban 413. Opus a 24 kbps deja el mismo audio en
/// ~0,18 MB/min (~10x más chico): una reunión de 15 min pasa de ~28 MB a ~3 MB y entra holgada.
///
/// Es solo para la COPIA que va a la nube: el archivo local del usuario se deja intacto. Opus/Ogg
/// lo transcribe el backend igual (Groq acepta ogg/opus) y lo reproduce cualquier navegador.
///
/// Reusa <see cref="AudioConverter"/> para llevar cualquier formato de entrada a WAV 16 kHz mono
/// 16-bit (mismo pipeline que Whisper), y recién ahí encodea a Opus.
/// </summary>
public sealed class AudioCompressor
{
    /// <summary>Opus soporta 16 kHz nativo (wideband); los audios ya vienen a 16 kHz mono.</summary>
    private const int SampleRate = AudioConverter.TargetSampleRate;

    /// <summary>24 kbps: voz clara. Subirlo mejora poco la voz y agranda el archivo.</summary>
    private const int BitrateBitsPerSecond = 24000;

    private readonly AudioConverter _converter;

    public AudioCompressor(AudioConverter? converter = null) => _converter = converter ?? new AudioConverter();

    /// <summary>
    /// Lee <paramref name="inputPath"/> (cualquier formato soportado) y escribe un Opus/Ogg mono en
    /// <paramref name="outputPath"/>.
    /// </summary>
    public void CompressToOpus(string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("No se encontró el audio de entrada.", inputPath);

        // 1) Cualquier formato -> WAV 16 kHz mono 16-bit (el converter ya maneja mp3/wav/ogg/m4a/webm...).
        var tempWav = Path.Combine(Path.GetTempPath(), $"atc_{Guid.NewGuid():N}.wav");
        try
        {
            _converter.ToWhisperWav(inputPath, tempWav, ct);
            EncodeWavToOpus(tempWav, outputPath, ct);
        }
        finally
        {
            if (File.Exists(tempWav))
                File.Delete(tempWav);
        }
    }

    private static void EncodeWavToOpus(string wavPath, string outputPath, CancellationToken ct)
    {
        using var reader = new WaveFileReader(wavPath); // WAV PCM 16 kHz mono 16-bit (lo garantiza ToWhisperWav)

        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = BitrateBitsPerSecond;

        using var outFile = File.Create(outputPath);
        var oggOut = new OpusOggWriteStream(encoder, outFile);

        // Lee el PCM de a ~1 segundo y lo escribe; OpusOggWriteStream se encarga del framing interno.
        var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var samples = new short[read / sizeof(short)];
            Buffer.BlockCopy(buffer, 0, samples, 0, read);
            oggOut.WriteSamples(samples, 0, samples.Length);
        }

        oggOut.Finish();
    }
}
