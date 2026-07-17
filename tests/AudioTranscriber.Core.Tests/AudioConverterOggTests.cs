using AudioTranscriber.Core.Audio;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using NAudio.Wave;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Verifica el soporte de OGG/Opus (formato de las notas de voz de WhatsApp),
/// que NAudio no decodifica y resolvemos con Concentus.
/// </summary>
public class AudioConverterOggTests : IDisposable
{
    private readonly string _dir;

    public AudioConverterOggTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_ogg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // Genera un OGG/Opus (48 kHz mono) con un tono, como el que produce WhatsApp.
    private string CreateOpusOgg(string name, double seconds = 0.4)
    {
        var path = Path.Combine(_dir, name);
        const int rate = 48000;
        var encoder = OpusCodecFactory.CreateEncoder(rate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);

        using var fileOut = File.Create(path);
        var oggOut = new OpusOggWriteStream(encoder, fileOut);

        int total = (int)(rate * seconds);
        var samples = new short[total];
        for (int i = 0; i < total; i++)
            samples[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);

        oggOut.WriteSamples(samples, 0, samples.Length);
        oggOut.Finish();
        return path;
    }

    [Fact]
    public void Convert_produces_16khz_mono_16bit_wav_from_opus_ogg()
    {
        var input = CreateOpusOgg("nota_wsp.ogg");
        var output = Path.Combine(_dir, "out.wav");

        new AudioConverter().ToWhisperWav(input, output);

        Assert.True(File.Exists(output));
        using var reader = new WaveFileReader(output);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.True(reader.Length > 0, "El WAV resultante no debería estar vacío.");
    }

    [Fact]
    public void Opus_extension_is_supported_too()
    {
        var input = CreateOpusOgg("nota.opus");
        var output = Path.Combine(_dir, "out2.wav");

        new AudioConverter().ToWhisperWav(input, output);

        using var reader = new WaveFileReader(output);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
    }
}
