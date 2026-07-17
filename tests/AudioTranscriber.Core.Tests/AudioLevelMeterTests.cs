using AudioTranscriber.Core.Audio;

namespace AudioTranscriber.Core.Tests;

public class AudioLevelMeterTests
{
    private static byte[] BuildPcm16(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void CalculateRms_BufferVacio_DevuelveCero()
    {
        Assert.Equal(0.0, AudioLevelMeter.CalculateRms(Array.Empty<byte>()));
    }

    [Fact]
    public void CalculateRms_SilencioTotal_DevuelveCero()
    {
        var bytes = BuildPcm16(0, 0, 0, 0, 0);
        Assert.Equal(0.0, AudioLevelMeter.CalculateRms(bytes));
    }

    [Fact]
    public void CalculateRms_AmplitudMaxima_DevuelveUno()
    {
        var bytes = BuildPcm16(short.MaxValue, short.MinValue, short.MaxValue, short.MinValue);
        var rms = AudioLevelMeter.CalculateRms(bytes);
        Assert.True(rms > 0.99 && rms <= 1.0, $"esperaba ~1.0, fue {rms}");
    }

    [Fact]
    public void CalculateRms_UnByteSuelto_SeIgnoraElByteImpar()
    {
        // 5 bytes = 2 samples completos + 1 byte colgado: no debe explotar ni contarlo.
        var full = BuildPcm16(1000, 1000);
        var withOddByte = new byte[full.Length + 1];
        Array.Copy(full, withOddByte, full.Length);
        withOddByte[^1] = 42;

        var expected = AudioLevelMeter.CalculateRms(full);
        var actual = AudioLevelMeter.CalculateRms(withOddByte);
        Assert.Equal(expected, actual, 6);
    }

    [Fact]
    public void CalculateRms_SonidoModerado_QuedaEntreCeroYUno()
    {
        var bytes = BuildPcm16(5000, -5000, 5000, -5000);
        var rms = AudioLevelMeter.CalculateRms(bytes);
        Assert.InRange(rms, 0.0, 1.0);
        Assert.True(rms > 0.1);
    }
}
