using AudioTranscriber.Core.Audio;
using AudioTranscriber.Core.Workspaces;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Verifica el soporte de mp4/m4a (extracción de audio con Media Foundation en Windows).
/// </summary>
public class AudioConverterMediaTests : IDisposable
{
    private readonly string _dir;

    public AudioConverterMediaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_media_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Mp4_and_m4a_are_supported_extensions()
    {
        Assert.Contains(".mp4", Workspace.SupportedExtensions);
        Assert.Contains(".m4a", Workspace.SupportedExtensions);
    }

    private string CreateWavPcm16(string name, int rate, int channels, double seconds)
    {
        var path = Path.Combine(_dir, name);
        var format = new WaveFormat(rate, 16, channels);
        using var writer = new WaveFileWriter(path, format);
        int total = (int)(rate * seconds);
        var buf = new short[total * channels];
        for (int i = 0; i < total; i++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
            for (int c = 0; c < channels; c++)
                buf[i * channels + c] = s;
        }
        writer.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    [Fact]
    public void Convert_produces_16khz_mono_16bit_wav_from_m4a()
    {
        // Genera un M4A (AAC) real vía Media Foundation. Si el encoder AAC no está
        // disponible en este entorno, el test no aplica (sale sin fallar).
        var wav = CreateWavPcm16("src.wav", 44100, 2, 0.5);
        var m4a = Path.Combine(_dir, "in.m4a");
        try
        {
            MediaFoundationApi.Startup();
            using var reader = new WaveFileReader(wav);
            MediaFoundationEncoder.EncodeToAac(reader, m4a, 128000);
        }
        catch
        {
            return; // sin encoder AAC en este entorno: no se puede armar el fixture
        }

        var output = Path.Combine(_dir, "out.wav");
        new AudioConverter().ToWhisperWav(m4a, output);

        using var outReader = new WaveFileReader(output);
        Assert.Equal(16000, outReader.WaveFormat.SampleRate);
        Assert.Equal(1, outReader.WaveFormat.Channels);
        Assert.Equal(16, outReader.WaveFormat.BitsPerSample);
        Assert.True(outReader.Length > 0);
    }
}
