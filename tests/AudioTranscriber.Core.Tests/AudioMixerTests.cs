using System;
using AudioTranscriber.Core.Audio;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class AudioMixerTests
{
    [Fact]
    public void MezclaLasDosFuentes()
    {
        var dest = new float[3];

        AudioMixer.MixInto(dest, new[] { 0.1f, 0.2f, 0.3f }, new[] { 0.01f, 0.02f, 0.03f });

        Assert.Equal(0.11f, dest[0], 5);
        Assert.Equal(0.22f, dest[1], 5);
        Assert.Equal(0.33f, dest[2], 5);
    }

    /// LA razón de ser del mixer: WASAPI loopback no entrega nada mientras no suena audio. Ese hueco
    /// tiene que ser silencio, no un salto en el tiempo.
    [Fact]
    public void SiUnaFuenteNoEntregoNada_SeRellenaConSilencio_SinPerderLaLineaDeTiempo()
    {
        var dest = new float[4];

        // El micrófono trajo 4 muestras; el sistema (nadie hablando) no trajo ninguna.
        AudioMixer.MixInto(dest, new[] { 0.5f, 0.5f, 0.5f, 0.5f }, ReadOnlySpan<float>.Empty);

        Assert.Equal(new[] { 0.5f, 0.5f, 0.5f, 0.5f }, dest);
    }

    [Fact]
    public void SiUnaFuenteVieneCorta_SoloEsaParteQuedaEnSilencio()
    {
        var dest = new float[4];

        AudioMixer.MixInto(dest, new[] { 0.5f, 0.5f, 0.5f, 0.5f }, new[] { 0.5f, 0.5f });

        Assert.Equal(new[] { 1.0f, 1.0f, 0.5f, 0.5f }, dest);
    }

    /// El destino es el metrónomo: siempre se llena entero, aunque sobren muestras en la fuente.
    [Fact]
    public void NuncaEscribeMasAllaDelDestino_AunqueLasFuentesTraiganDeMas()
    {
        var dest = new float[2];

        AudioMixer.MixInto(dest, new[] { 0.1f, 0.1f, 0.9f, 0.9f }, new[] { 0.1f, 0.1f, 0.9f });

        Assert.Equal(new[] { 0.2f, 0.2f }, dest);
    }

    [Fact]
    public void SinNingunaFuente_EscribeSilencio()
    {
        var dest = new float[] { 9f, 9f };

        AudioMixer.MixInto(dest, ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty);

        Assert.Equal(new[] { 0f, 0f }, dest);
    }

    [Fact]
    public void DestinoVacio_NoRompe()
    {
        AudioMixer.MixInto(Span<float>.Empty, new[] { 1f }, new[] { 1f });
    }

    // ---- Clamp ----

    /// Dos personas hablando fuerte a la vez pasan de 1.0. Sin acotar, al pasar a PCM de 16 bits el
    /// valor da la vuelta y se escucha un chasquido.
    [Fact]
    public void AcotaLosPicos_EnVezDeDejarQueElValorDeLaVuelta()
    {
        var dest = new float[2];

        AudioMixer.MixInto(dest, new[] { 0.9f, -0.9f }, new[] { 0.8f, -0.8f });

        Assert.Equal(1f, dest[0]);
        Assert.Equal(-1f, dest[1]);
    }

    [Fact]
    public void NoAtenuaCuandoNoHaceFalta()
    {
        // Un audio bajo transcribe peor: no se divide por 2 "por las dudas".
        var dest = new float[1];

        AudioMixer.MixInto(dest, new[] { 0.4f }, new[] { 0.4f });

        Assert.Equal(0.8f, dest[0], 5);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void UnDriverQueEntregaBasura_NoEnvenenaElArchivo(float garbage)
    {
        Assert.Equal(0f, AudioMixer.Clamp(garbage));
    }

    // ---- SamplesOwed (el metrónomo) ----

    [Fact]
    public void CalculaLasMuestrasQueCorrespondenAlRelojDePared()
    {
        // 1 segundo a 16 kHz = 16.000 muestras.
        Assert.Equal(16_000, AudioMixer.SamplesOwed(TimeSpan.FromSeconds(1), 16_000, alreadyWritten: 0));
    }

    [Fact]
    public void DescuentaLoQueYaSeEscribio()
    {
        Assert.Equal(6_000, AudioMixer.SamplesOwed(TimeSpan.FromSeconds(1), 16_000, alreadyWritten: 10_000));
    }

    /// Si el archivo va ADELANTADO del reloj, no se borra nada ya escrito: se espera.
    [Fact]
    public void NuncaDevuelveNegativo()
    {
        Assert.Equal(0, AudioMixer.SamplesOwed(TimeSpan.FromSeconds(1), 16_000, alreadyWritten: 99_999));
    }

    /// La prueba de la deriva: después de 1,5 horas el total sigue atado al reloj, no a los buffers.
    [Fact]
    public void EnUnaReunionDe90Minutos_ElTotalSigueElRelojDePared()
    {
        var elapsed = TimeSpan.FromMinutes(90);
        var expected = (long)(elapsed.TotalSeconds * 16_000); // 86.400.000 muestras

        Assert.Equal(expected, AudioMixer.SamplesOwed(elapsed, 16_000, alreadyWritten: 0));
    }

    [Fact]
    public void RechazaUnSampleRateInvalido_EnVezDeGenerarBasura()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioMixer.SamplesOwed(TimeSpan.FromSeconds(1), 0, 0));
    }
}
