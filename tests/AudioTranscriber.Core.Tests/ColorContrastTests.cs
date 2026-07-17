using System;
using AudioTranscriber.Core.Common;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class ColorContrastTests
{
    /// Los dos extremos conocidos de la escala: si estos fallan, la fórmula está mal.
    [Fact]
    public void NegroContraBlanco_Es21()
    {
        Assert.Equal(21.0, ColorContrast.Ratio("#000000", "#FFFFFF"), 1);
    }

    [Fact]
    public void UnColorContraSiMismo_Es1()
    {
        Assert.Equal(1.0, ColorContrast.Ratio("#6366F1", "#6366F1"), 3);
    }

    [Fact]
    public void ElOrdenDeLosArgumentosNoImporta()
    {
        Assert.Equal(
            ColorContrast.Ratio("#F1F2F6", "#24222E"),
            ColorContrast.Ratio("#24222E", "#F1F2F6"),
            5);
    }

    /// Valor de referencia externo: gris medio contra blanco, publicado en la doc de WCAG.
    [Fact]
    public void CoincideConUnValorConocidoDeReferencia()
    {
        // #767676 sobre blanco es el gris más oscuro que WCAG documenta como el límite de AA (4.54:1).
        Assert.Equal(4.54, ColorContrast.Ratio("#767676", "#FFFFFF"), 2);
    }

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#ffffff")]
    [InlineData("ffffff")]
    [InlineData("#FFFFFFFF")] // #AARRGGBB — el alfa se ignora
    public void AceptaLosFormatosQueUsaWpf(string hex)
    {
        Assert.Equal(21.0, ColorContrast.Ratio(hex, "#000000"), 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("rojo")]
    public void RechazaUnColorInvalido_EnVezDeDevolverBasura(string hex)
    {
        Assert.ThrowsAny<Exception>(() => ColorContrast.Ratio(hex, "#000000"));
    }

    // ---- Opacidad: el caso que hizo invisible al anillo de foco ----

    /// Opacidad 1 = el color tal cual.
    [Fact]
    public void ConOpacidadUno_EsIgualAlRatioNormal()
    {
        Assert.Equal(
            ColorContrast.Ratio("#6366F1", "#24222E"),
            ColorContrast.RatioWithOpacity("#6366F1", 1.0, "#24222E"),
            3);
    }

    /// Opacidad 0 = el color desaparece: queda el fondo contra sí mismo.
    [Fact]
    public void ConOpacidadCero_ElColorDesaparece()
    {
        Assert.Equal(1.0, ColorContrast.RatioWithOpacity("#6366F1", 0.0, "#24222E"), 3);
    }

    /// EL bug real: el anillo de foco declaraba el acento (que contrasta bien sobre papel), pero a
    /// 0.55 de opacidad sobre Surface terminaba en ~1.96:1 — invisible, aunque el comentario del
    /// código dijera "un anillo de foco de teclado VISIBLE (accesibilidad)".
    [Fact]
    public void ElAnilloDeFocoViejo_EraInvisible()
    {
        var real = ColorContrast.RatioWithOpacity("#6366F1", 0.55, "#24222E");

        Assert.True(real < ColorContrast.AaLargeTextOrUi,
            $"El anillo viejo debería fallar AA para UI (3:1); dio {real:F2}. Si esto pasa, cambió la paleta.");
        Assert.Equal(1.96, real, 1);
    }

    /// Y la prueba de que la opacidad ES el problema: el MISMO color sin opacidad pasa cómodo.
    [Fact]
    public void ElMismoColorSinOpacidad_MejoraMucho()
    {
        var conOpacidad = ColorContrast.RatioWithOpacity("#6366F1", 0.55, "#24222E");
        var sinOpacidad = ColorContrast.Ratio("#6366F1", "#24222E");

        Assert.True(sinOpacidad > conOpacidad * 1.5);
    }

    [Fact]
    public void RechazaUnaOpacidadFueraDeRango()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorContrast.RatioWithOpacity("#FFFFFF", 1.5, "#000000"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorContrast.RatioWithOpacity("#FFFFFF", -0.1, "#000000"));
    }
}
