using AudioTranscriber.Core.Audio;
using NAudio.Wave;

namespace AudioTranscriber.Core.Tests;

public class AudioCompressorTests : IDisposable
{
    private readonly string _dir;

    public AudioCompressorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "atc_comp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // Genera un WAV 16 kHz mono 16-bit con un tono (no silencio: el silencio comprime irrealmente).
    private string WriteToneWav(double seconds)
    {
        var path = Path.Combine(_dir, "in.wav");
        var totalSamples = (int)(AudioConverter.TargetSampleRate * seconds);
        var bytes = new byte[totalSamples * sizeof(short)];
        for (var i = 0; i < totalSamples; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * 440 * i / AudioConverter.TargetSampleRate) * 8000);
            bytes[i * 2] = (byte)(s & 0xff);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }

        using var writer = new WaveFileWriter(path, new WaveFormat(AudioConverter.TargetSampleRate, 16, 1));
        writer.Write(bytes, 0, bytes.Length);
        return path;
    }

    [Fact]
    public void CompressToOpus_produces_a_much_smaller_file()
    {
        var wav = WriteToneWav(seconds: 5);
        var opus = Path.Combine(_dir, "out.ogg");

        new AudioCompressor().CompressToOpus(wav, opus);

        Assert.True(File.Exists(opus));
        var wavSize = new FileInfo(wav).Length;
        var opusSize = new FileInfo(opus).Length;
        // 5 s a 16 kHz mono 16-bit = ~160 KB; a 24 kbps opus = ~15 KB. Con margen: al menos 3x más chico.
        Assert.True(opusSize * 3 < wavSize,
            $"Opus ({opusSize} bytes) debería ser bastante más chico que el WAV ({wavSize} bytes).");
    }

    [Fact]
    public void CompressToOpus_output_is_valid_and_roundtrips_to_similar_duration()
    {
        var wav = WriteToneWav(seconds: 5);
        var opus = Path.Combine(_dir, "out.ogg");
        new AudioCompressor().CompressToOpus(wav, opus);

        // El opus tiene que ser un ogg válido: decodificarlo de vuelta (mismo camino que usa la app
        // para las notas de voz) no debe fallar y debe conservar la duración.
        var back = Path.Combine(_dir, "back.wav");
        new AudioConverter().ToWhisperWav(opus, back);

        using var reader = new WaveFileReader(back);
        Assert.InRange(reader.TotalTime.TotalSeconds, 4.3, 5.7);
    }

    [Fact]
    public void CompressToOpus_leaves_the_source_file_untouched()
    {
        var wav = WriteToneWav(seconds: 2);
        var before = File.ReadAllBytes(wav);

        new AudioCompressor().CompressToOpus(wav, Path.Combine(_dir, "out.ogg"));

        Assert.Equal(before, File.ReadAllBytes(wav)); // la copia local del usuario queda intacta
    }

    [Fact]
    public void CompressToOpus_throws_a_clear_error_when_input_is_missing()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new AudioCompressor().CompressToOpus(Path.Combine(_dir, "no-existe.wav"), Path.Combine(_dir, "out.ogg")));
    }
}
