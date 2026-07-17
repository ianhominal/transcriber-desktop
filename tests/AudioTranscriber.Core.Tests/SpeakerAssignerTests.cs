using System;
using System.Collections.Generic;
using AudioTranscriber.Core.Diarization;
using AudioTranscriber.Core.Transcription;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class SpeakerAssignerTests
{
    private static TranscriptSegment Seg(double from, double to, string text = "hola") =>
        new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), text);

    private static SpeakerSegment Spk(double from, double to, int speaker) =>
        new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), speaker);

    [Fact]
    public void SegmentoDentroDeUnHablante_SeLeAtribuyeAEse()
    {
        var result = SpeakerAssigner.Assign(
            new[] { Seg(1, 2) },
            new[] { Spk(0, 10, 0) });

        Assert.Equal(0, result[0].Speaker);
    }

    /// El caso que importa: Whisper y el diarizador cortan distinto, así que un segmento de texto
    /// pisa a dos hablantes. Gana el que más tiempo comparte.
    [Fact]
    public void SegmentoAPuntoDeDosHablantes_GanaElDeMayorSolapamiento()
    {
        // 0→10: habla 0 hasta el segundo 8, después habla 1. El texto va de 5 a 10:
        // 3s con el hablante 0, 2s con el 1 → gana el 0.
        var result = SpeakerAssigner.Assign(
            new[] { Seg(5, 10) },
            new[] { Spk(0, 8, 0), Spk(8, 20, 1) });

        Assert.Equal(0, result[0].Speaker);
    }

    [Fact]
    public void CuandoElOtroHablanteTieneMasTiempo_GanaElOtro()
    {
        // El texto va de 5 a 10: 1s con el hablante 0, 4s con el 1 → gana el 1.
        var result = SpeakerAssigner.Assign(
            new[] { Seg(5, 10) },
            new[] { Spk(0, 6, 0), Spk(6, 20, 1) });

        Assert.Equal(1, result[0].Speaker);
    }

    [Fact]
    public void SegmentoQueNoPisaANadie_QuedaSinHablante_NoSeLeInventaUno()
    {
        var result = SpeakerAssigner.Assign(
            new[] { Seg(50, 60) },
            new[] { Spk(0, 10, 0) });

        Assert.Null(result[0].Speaker);
    }

    [Fact]
    public void RangosQueSoloSeTocanEnUnPunto_NoCuentanComoSolapamiento()
    {
        // El hablante termina justo donde arranca el texto: 0 segundos compartidos.
        var result = SpeakerAssigner.Assign(
            new[] { Seg(10, 20) },
            new[] { Spk(0, 10, 0) });

        Assert.Null(result[0].Speaker);
    }

    [Fact]
    public void SinHablantes_TodoQuedaSinAtribuir_YNoRompe()
    {
        var result = SpeakerAssigner.Assign(
            new[] { Seg(0, 5), Seg(5, 10) },
            Array.Empty<SpeakerSegment>());

        Assert.Equal(2, result.Count);
        Assert.All(result, s => Assert.Null(s.Speaker));
    }

    [Fact]
    public void SinTranscripcion_DevuelveVacio()
    {
        var result = SpeakerAssigner.Assign(Array.Empty<TranscriptSegment>(), new[] { Spk(0, 10, 0) });

        Assert.Empty(result);
    }

    [Fact]
    public void ConservaElTextoYLosTiemposDeCadaSegmento()
    {
        var result = SpeakerAssigner.Assign(
            new[] { Seg(1, 2, "el scope del vertical slice") },
            new[] { Spk(0, 10, 3) });

        Assert.Equal("el scope del vertical slice", result[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), result[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2), result[0].End);
    }

    [Fact]
    public void EmpateExacto_ResuelveDeFormaEstable_AlPrimero()
    {
        // 5s con cada uno. Sea cual sea el ganador, tiene que ser SIEMPRE el mismo.
        var speakers = new[] { Spk(0, 10, 0), Spk(10, 20, 1) };
        var first = SpeakerAssigner.Assign(new[] { Seg(5, 15) }, speakers)[0].Speaker;
        var second = SpeakerAssigner.Assign(new[] { Seg(5, 15) }, speakers)[0].Speaker;

        Assert.Equal(first, second);
        Assert.Equal(0, first);
    }

    [Fact]
    public void DistinctSpeakers_CuentaSoloLosAtribuidos()
    {
        var labeled = SpeakerAssigner.Assign(
            new[] { Seg(0, 5), Seg(5, 10), Seg(50, 55) },
            new[] { Spk(0, 5, 0), Spk(5, 10, 1) });

        // Dos hablantes reales; el tercer segmento no pisa a nadie y no debe inflar la cuenta.
        Assert.Equal(2, SpeakerAssigner.DistinctSpeakers(labeled));
    }

    [Fact]
    public void DistinctSpeakers_SinAtribuciones_EsCero()
    {
        var labeled = SpeakerAssigner.Assign(new[] { Seg(0, 5) }, Array.Empty<SpeakerSegment>());

        Assert.Equal(0, SpeakerAssigner.DistinctSpeakers(labeled));
    }
}
