using AudioTranscriber.Core.Transcription;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Red de seguridad para el texto YA transcrito: aunque se corte el streaming al detectar el loop
/// (ver <see cref="RepetitionLoopDetectorTests"/>), lo que se alcanzó a acumular puede traer la
/// frase alucinada repetida decenas de veces. Esto la colapsa antes de guardar.
/// </summary>
public class TranscriptSanitizerTests
{
    [Fact]
    public void DejaIntactoUnTextoNormal()
    {
        const string texto = "Hola, buenas tardes. Hoy vamos a hablar del proyecto. Primero, el presupuesto.";

        Assert.Equal(texto, TranscriptSanitizer.CollapseRepeatedSentences(texto));
    }

    [Fact]
    public void ColapsaLaFraseAlucinadaRepetida()
    {
        var texto = string.Join(" ", Enumerable.Repeat("Mae'r cydnabod yn dda iawn.", 40));

        var limpio = TranscriptSanitizer.CollapseRepeatedSentences(texto);

        Assert.Equal("Mae'r cydnabod yn dda iawn.", limpio);
    }

    [Fact]
    public void ConservaLoQueVieneAntesYDespuesDelLoop()
    {
        var texto = "Arranca la reunión. " + string.Join(" ", Enumerable.Repeat("Repetido sin sentido.", 30)) + " Cierre final.";

        var limpio = TranscriptSanitizer.CollapseRepeatedSentences(texto);

        Assert.Equal("Arranca la reunión. Repetido sin sentido. Cierre final.", limpio);
    }

    [Fact]
    public void NoTocaRepeticionesCortasLegitimas()
    {
        // "Sí. Sí." es habla real, no una alucinación: el umbral tiene que dejarla pasar.
        const string texto = "Sí. Sí. Claro que sí.";

        Assert.Equal(texto, TranscriptSanitizer.CollapseRepeatedSentences(texto));
    }

    [Fact]
    public void ComparaIgnorandoMayusculasYEspacios()
    {
        var texto = string.Join(" ", Enumerable.Repeat("Hola.   HOLA. hola.", 10));

        var limpio = TranscriptSanitizer.CollapseRepeatedSentences(texto);

        // Todas son la misma oración: queda una sola, con la forma de la primera aparición.
        Assert.Equal("Hola.", limpio);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToleraTextoVacio(string texto)
    {
        Assert.Equal(texto, TranscriptSanitizer.CollapseRepeatedSentences(texto));
    }

    [Fact]
    public void ToleraNull()
    {
        Assert.Null(TranscriptSanitizer.CollapseRepeatedSentences(null));
    }

    [Fact]
    public void DetectaSiUnTextoEsMayormenteUnLoop()
    {
        var loop = string.Join(" ", Enumerable.Repeat("Frase alucinada.", 50));
        const string normal = "Hola, buenas tardes. Hoy vamos a hablar del proyecto.";

        Assert.True(TranscriptSanitizer.LooksLikeHallucinationLoop(loop));
        Assert.False(TranscriptSanitizer.LooksLikeHallucinationLoop(normal));
    }
}
