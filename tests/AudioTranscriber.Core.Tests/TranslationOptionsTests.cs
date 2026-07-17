using AudioTranscriber.Core.Transcription;

namespace AudioTranscriber.Core.Tests;

public class TranslationOptionsTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("pt")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("de")]
    public void ResolveLanguage_CodigoEnLaAllowlist_LoDevuelveTalCual(string code)
    {
        Assert.Equal(code, TranslationOptions.ResolveLanguage(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("jp")]
    [InlineData("cualquier-cosa")]
    public void ResolveLanguage_CodigoFueraDeLaAllowlistONulo_CaeAlDefault(string? code)
    {
        Assert.Equal(TranslationOptions.DefaultLanguage, TranslationOptions.ResolveLanguage(code));
    }

    [Fact]
    public void DefaultLanguage_EsIngles()
    {
        // Mismo default que audio-transcriber-web/src/lib/translate/languages.ts
        // (DEFAULT_TRANSLATION_LANGUAGE = "en"): traducir "es a es" no tendría sentido.
        Assert.Equal("en", TranslationOptions.DefaultLanguage);
    }

    [Fact]
    public void Languages_TieneLosSeisIdiomasDeLaWebConSusLabels()
    {
        var byCode = TranslationOptions.Languages.ToDictionary(l => l.Code, l => l.Label);

        Assert.Equal(6, TranslationOptions.Languages.Count);
        Assert.Equal("Español", byCode["es"]);
        Assert.Equal("Inglés", byCode["en"]);
        Assert.Equal("Portugués", byCode["pt"]);
        Assert.Equal("Francés", byCode["fr"]);
        Assert.Equal("Italiano", byCode["it"]);
        Assert.Equal("Alemán", byCode["de"]);
    }

    [Theory]
    [InlineData("translate", true)]
    [InlineData("TRANSLATE", true)]
    [InlineData("transcribe", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("otro", false)]
    public void IsTranslateMode_SoloTranslateEsTrue(string? mode, bool expected)
    {
        Assert.Equal(expected, TranslationOptions.IsTranslateMode(mode));
    }
}
