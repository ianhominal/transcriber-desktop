using AudioTranscriber.Core.Audio;
using NAudio.Wave;

namespace AudioTranscriber.Core.Tests;

public class AudioConverterTests : IDisposable
{
    private readonly string _dir;

    public AudioConverterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_conv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // Genera un WAV sintetico (tono) con el formato pedido, para usar como entrada.
    private string CreateWav(string name, int sampleRate, int channels, double seconds = 0.2)
    {
        var path = Path.Combine(_dir, name);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        using var writer = new WaveFileWriter(path, format);
        int totalSamples = (int)(sampleRate * seconds);
        for (int i = 0; i < totalSamples; i++)
        {
            float sample = (float)Math.Sin(2 * Math.PI * 440 * i / sampleRate);
            for (int c = 0; c < channels; c++)
                writer.WriteSample(sample);
        }
        return path;
    }

    [Fact]
    public void Convert_produces_16khz_mono_16bit_wav_from_stereo_44100()
    {
        var input = CreateWav("input_stereo.wav", sampleRate: 44100, channels: 2);
        var output = Path.Combine(_dir, "out.wav");

        new AudioConverter().ToWhisperWav(input, output);

        Assert.True(File.Exists(output));
        using var reader = new WaveFileReader(output);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
    }

    [Fact]
    public void Convert_produces_16khz_mono_from_mono_16000_already_correct()
    {
        var input = CreateWav("input_mono.wav", sampleRate: 16000, channels: 1);
        var output = Path.Combine(_dir, "out2.wav");

        new AudioConverter().ToWhisperWav(input, output);

        using var reader = new WaveFileReader(output);
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        // Debe tener audio real (no vacio)
        Assert.True(reader.Length > 0);
    }

    [Fact]
    public void Convert_throws_when_input_does_not_exist()
    {
        var converter = new AudioConverter();
        Assert.Throws<FileNotFoundException>(
            () => converter.ToWhisperWav(Path.Combine(_dir, "no_existe.wav"), Path.Combine(_dir, "x.wav")));
    }
}
