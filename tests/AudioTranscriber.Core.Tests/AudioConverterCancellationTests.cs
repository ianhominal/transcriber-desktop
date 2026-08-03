using AudioTranscriber.Core.Audio;
using NAudio.Wave;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Cancelar durante "Preparando… (convirtiendo audio y cargando modelo)" no hacía nada.
///
/// El token llegaba a <c>ToWhisperWav</c> pero solo el camino de OGG/Opus lo miraba: mp3/wav y
/// mp4/m4a/aac/webm terminaban en <c>WaveFileWriter.CreateWaveFile16</c>, que lee el stream entero
/// y escribe el WAV de una, sin ningún punto de cancelación. El botón disparaba el token y nadie lo
/// estaba mirando hasta que arrancaba la transcripción.
/// </summary>
public class AudioConverterCancellationTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "at_conv_" + Guid.NewGuid().ToString("N"));

    public AudioConverterCancellationTests() => Directory.CreateDirectory(_carpeta);

    /// <summary>Genera un WAV real (PCM 16-bit, 44,1 kHz estéreo) para no depender de archivos de prueba.</summary>
    private string CrearWav(string nombre, int segundos)
    {
        var ruta = Path.Combine(_carpeta, nombre);
        var formato = new WaveFormat(44100, 16, 2);
        using var writer = new WaveFileWriter(ruta, formato);
        var muestras = new short[formato.SampleRate * formato.Channels];
        for (var i = 0; i < muestras.Length; i++)
            muestras[i] = (short)(Math.Sin(i * 0.01) * 8000);
        var bytes = new byte[muestras.Length * sizeof(short)];
        Buffer.BlockCopy(muestras, 0, bytes, 0, bytes.Length);
        for (var s = 0; s < segundos; s++)
            writer.Write(bytes, 0, bytes.Length);
        return ruta;
    }

    [Fact]
    public void ConvierteUnWavAMonoDe16kHz()
    {
        var entrada = CrearWav("entrada.wav", segundos: 2);
        var salida = Path.Combine(_carpeta, "salida.wav");

        new AudioConverter().ToWhisperWav(entrada, salida, CancellationToken.None);

        using var lector = new WaveFileReader(salida);
        Assert.Equal(16000, lector.WaveFormat.SampleRate);
        Assert.Equal(1, lector.WaveFormat.Channels);
        Assert.Equal(16, lector.WaveFormat.BitsPerSample);
        // Dos segundos de audio siguen siendo dos segundos después del resampleo.
        Assert.InRange(lector.TotalTime.TotalSeconds, 1.8, 2.2);
    }

    [Fact]
    public void UnTokenYaCanceladoCortaLaConversionDeWav()
    {
        var entrada = CrearWav("cancelar.wav", segundos: 3);
        var salida = Path.Combine(_carpeta, "cancelar-salida.wav");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new AudioConverter().ToWhisperWav(entrada, salida, cts.Token));
    }

    [Fact]
    public async Task CancelarAMitadDeCaminoTambienCorta()
    {
        var entrada = CrearWav("mitad.wav", segundos: 5);
        var salida = Path.Combine(_carpeta, "mitad-salida.wav");
        using var cts = new CancellationTokenSource();

        // Se cancela desde otro hilo mientras la conversión corre: es lo que pasa de verdad cuando
        // alguien aprieta "Cancelar" con la conversión ya empezada.
        var tarea = Task.Run(() => new AudioConverter().ToWhisperWav(entrada, salida, cts.Token));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tarea);
    }

    [Fact]
    public void SinCancelacionNoTiraAunqueElTokenExista()
    {
        var entrada = CrearWav("ok.wav", segundos: 1);
        var salida = Path.Combine(_carpeta, "ok-salida.wav");
        using var cts = new CancellationTokenSource();

        new AudioConverter().ToWhisperWav(entrada, salida, cts.Token);

        Assert.True(File.Exists(salida));
        Assert.True(new FileInfo(salida).Length > 0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_carpeta))
                Directory.Delete(_carpeta, recursive: true);
        }
        catch
        {
            // Limpieza best-effort: un archivo todavía tomado no puede voltear el test.
        }
    }
}
