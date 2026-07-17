using System;
using AudioTranscriber.Core.Audio;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class ChannelDownmixerTests
{
    [Fact]
    public void UnSoloCanal_CopiaDirecto()
    {
        var dest = new float[3];

        int frames = ChannelDownmixer.ToMono(new[] { 0.1f, 0.2f, 0.3f }, channels: 1, dest);

        Assert.Equal(3, frames);
        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, dest);
    }

    [Fact]
    public void Estereo_PromediaCanalIzquierdoYDerecho()
    {
        var dest = new float[2];

        // Frame 0: L=1.0 R=0.0 -> 0.5. Frame 1: L=0.2 R=0.4 -> 0.3.
        int frames = ChannelDownmixer.ToMono(new[] { 1.0f, 0.0f, 0.2f, 0.4f }, channels: 2, dest);

        Assert.Equal(2, frames);
        Assert.Equal(0.5f, dest[0], 5);
        Assert.Equal(0.3f, dest[1], 5);
    }

    /// Placas 5.1/7.1: el downmix no está atado a estéreo, promedia cualquier cantidad de canales.
    [Fact]
    public void CuatroCanales_PromediaLosCuatro()
    {
        var dest = new float[1];

        int frames = ChannelDownmixer.ToMono(new[] { 1f, 1f, 1f, 1f }, channels: 4, dest);

        Assert.Equal(1, frames);
        Assert.Equal(1f, dest[0], 5);
    }

    /// Nadie desaparece del audio: promediar (no "quedarse con el canal izquierdo") conserva a
    /// quien suena solo por un canal.
    [Fact]
    public void UnaVozSoloEnUnCanal_NoDesaparece()
    {
        var dest = new float[1];

        int frames = ChannelDownmixer.ToMono(new[] { 0f, 0.8f }, channels: 2, dest);

        Assert.Equal(1, frames);
        Assert.Equal(0.4f, dest[0], 5);
    }

    [Fact]
    public void UnaMuestraFinalIncompleta_SeDescarta_NoLeeFueraDeRango()
    {
        var dest = new float[2];

        // 5 floats para canales=2: el último frame (índice 4) quedaría incompleto -- se descarta.
        int frames = ChannelDownmixer.ToMono(new[] { 1f, 1f, 0.5f, 0.5f, 9f }, channels: 2, dest);

        Assert.Equal(2, frames);
        Assert.Equal(1f, dest[0], 5);
        Assert.Equal(0.5f, dest[1], 5);
    }

    [Fact]
    public void DestinoMasChicoQueLosFramesDisponibles_SeAcota()
    {
        var dest = new float[1];

        int frames = ChannelDownmixer.ToMono(new[] { 1f, 1f, 0.5f, 0.5f }, channels: 2, dest);

        Assert.Equal(1, frames);
        Assert.Equal(1f, dest[0], 5);
    }

    [Fact]
    public void SinMuestras_DevuelveCero()
    {
        var dest = new float[2];

        int frames = ChannelDownmixer.ToMono(ReadOnlySpan<float>.Empty, channels: 2, dest);

        Assert.Equal(0, frames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CanalesInvalidos_Tira(int channels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelDownmixer.ToMono(new[] { 1f }, channels, new float[1]));
    }
}
