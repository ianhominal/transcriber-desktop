using System;
using AudioTranscriber.Core.Diarization;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class SpeakerTranscriptFormatterTests
{
    private static LabeledSegment Seg(double from, double to, string text, int? speaker) =>
        new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), text, speaker);

    [Fact]
    public void SpeakerLabel_CuentaDesdeUno_NoDesdeCero()
    {
        Assert.Equal("Persona 1", SpeakerTranscriptFormatter.SpeakerLabel(0));
        Assert.Equal("Persona 2", SpeakerTranscriptFormatter.SpeakerLabel(1));
    }

    [Fact]
    public void SpeakerLabel_SinHablante_NoInventaUnNombre()
    {
        Assert.Equal("Sin identificar", SpeakerTranscriptFormatter.SpeakerLabel(null));
    }

    /// La razón de ser del formateador: el diarizador corta cada pocos segundos y una línea
    /// "Persona 1:" por frase sería ilegible.
    [Fact]
    public void SegmentosSeguidosDelMismoHablante_SeJuntanEnUnSoloBloque()
    {
        var text = SpeakerTranscriptFormatter.Format(new[]
        {
            Seg(0, 2, "bueno", 0),
            Seg(2, 4, "esto sería el pitch", 0),
            Seg(4, 6, "de Cadejos", 0),
        });

        Assert.Equal("Persona 1: bueno esto sería el pitch de Cadejos", text);
    }

    [Fact]
    public void AlCambiarDeHablante_ArrancaUnBloqueNuevo()
    {
        var text = SpeakerTranscriptFormatter.Format(new[]
        {
            Seg(0, 2, "esto sería el pitch", 0),
            Seg(2, 4, "¿cuál es el scope?", 1),
        });

        Assert.Equal("Persona 1: esto sería el pitch\n\nPersona 2: ¿cuál es el scope?", text);
    }

    [Fact]
    public void SiUnHablanteVuelveAHablar_SeAbreOtroBloqueSuyo()
    {
        var text = SpeakerTranscriptFormatter.Format(new[]
        {
            Seg(0, 2, "hola", 0),
            Seg(2, 4, "hola", 1),
            Seg(4, 6, "sigo yo", 0),
        });

        Assert.Equal("Persona 1: hola\n\nPersona 2: hola\n\nPersona 1: sigo yo", text);
    }

    [Fact]
    public void LosSegmentosSinHablante_SeMarcanComoSinIdentificar()
    {
        var text = SpeakerTranscriptFormatter.Format(new[] { Seg(0, 2, "ruido", null) });

        Assert.Equal("Sin identificar: ruido", text);
    }

    [Fact]
    public void SinIdentificar_TambienSeAgrupaComoBloque()
    {
        var text = SpeakerTranscriptFormatter.Format(new[]
        {
            Seg(0, 2, "uno", null),
            Seg(2, 4, "dos", null),
        });

        Assert.Equal("Sin identificar: uno dos", text);
    }

    [Fact]
    public void IgnoraSegmentosVacios_SinAbrirBloquesFantasma()
    {
        var text = SpeakerTranscriptFormatter.Format(new[]
        {
            Seg(0, 2, "hola", 0),
            Seg(2, 3, "   ", 1),
            Seg(3, 4, "chau", 0),
        });

        Assert.Equal("Persona 1: hola chau", text);
    }

    [Fact]
    public void SinSegmentos_DevuelveVacio()
    {
        Assert.Equal("", SpeakerTranscriptFormatter.Format(Array.Empty<LabeledSegment>()));
    }

    [Fact]
    public void NoDejaEspaciosNiSaltosColgandoAlFinal()
    {
        var text = SpeakerTranscriptFormatter.Format(new[] { Seg(0, 2, "  hola  ", 0) });

        Assert.Equal("Persona 1: hola", text);
    }

    // ---- Caso real reportado: "Persona 1" y "Persona 3", sin Persona 2 ----

    /// Los ids del diarizador son arbitrarios y no todos terminan con texto. Sobreviven 0 y 2 →
    /// antes se veía "Persona 1" y "Persona 3", y el usuario buscaba una Persona 2 inexistente.
    [Fact]
    public void IdsNoConsecutivosDelDiarizador_SeMuestranComoPersonasConsecutivas()
    {
        var text = SpeakerTranscriptFormatter.Format(new[]
        {
            Seg(0, 2, "hola", 0),
            Seg(2, 4, "chau", 2),
        });

        Assert.Equal("Persona 1: hola\n\nPersona 2: chau", text);
        Assert.DoesNotContain("Persona 3", text);
    }

    [Fact]
    public void RenumerarRespetaElOrdenDeAparicion_NoElValorDelId()
    {
        var text = SpeakerTranscriptFormatter.Format(new[]
        {
            Seg(0, 2, "habla el 7", 7),
            Seg(2, 4, "habla el 3", 3),
            Seg(4, 6, "vuelve el 7", 7),
        });

        Assert.Equal("Persona 1: habla el 7\n\nPersona 2: habla el 3\n\nPersona 1: vuelve el 7", text);
    }

    /// Caso real: un tramo de música quedó como "Persona 3: [MÚSICA]" — esa persona no existía.
    [Fact]
    public void MarcasDeNoHabla_NoSeLeAtribuyenANadie_NiConsumenUnNumeroDePersona()
    {
        var text = SpeakerTranscriptFormatter.Format(new[]
        {
            Seg(0, 2, "hola", 0),
            Seg(2, 4, "[MÚSICA]", 2),
            Seg(4, 6, "chau", 1),
        });

        Assert.DoesNotContain("Persona 3", text);
        Assert.Contains("Persona 1: hola", text);
        Assert.Contains("Persona 2: chau", text);
    }

    [Theory]
    [InlineData("[MÚSICA]")]
    [InlineData("[SILENCIO]")]
    [InlineData("[Music]")]
    [InlineData("(música)")]
    [InlineData("  [BLANK_AUDIO]  ")]
    public void ReconoceLasMarcasDeNoHabla(string marker)
    {
        Assert.True(SpeakerTranscriptFormatter.IsNonSpeechMarker(marker));
    }

    [Theory]
    [InlineData("hola que tal")]
    [InlineData("[MÚSICA] y ahí va")]   // tiene habla real después de la marca
    [InlineData("")]
    public void NoConfundeHablaRealConUnaMarca(string text)
    {
        Assert.False(SpeakerTranscriptFormatter.IsNonSpeechMarker(text));
    }
}
