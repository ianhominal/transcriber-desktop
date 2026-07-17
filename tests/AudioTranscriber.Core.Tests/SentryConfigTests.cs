using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.Core.Tests;

public class SentryConfigTests
{
    [Fact]
    public void DsnNulo_SentryQueda_Deshabilitado()
    {
        Assert.False(SentryConfig.IsEnabled(null));
    }

    [Fact]
    public void DsnVacio_SentryQueda_Deshabilitado()
    {
        Assert.False(SentryConfig.IsEnabled(string.Empty));
    }

    [Fact]
    public void DsnSoloEspacios_SentryQueda_Deshabilitado()
    {
        Assert.False(SentryConfig.IsEnabled("   "));
    }

    [Fact]
    public void DsnEsElPlaceholderSinReemplazar_SentryQueda_Deshabilitado()
    {
        Assert.False(SentryConfig.IsEnabled(SentryConfig.PlaceholderDsn));
    }

    [Fact]
    public void DsnEsElPlaceholderConEspaciosAlrededor_SentryQueda_Deshabilitado()
    {
        Assert.False(SentryConfig.IsEnabled($"  {SentryConfig.PlaceholderDsn}  "));
    }

    [Fact]
    public void DsnEsElPlaceholderEnMinusculas_SentryQueda_Deshabilitado()
    {
        // El chequeo no debe ser case-sensitive: alguien podría tipear "sentry_dsn" a mano.
        Assert.False(SentryConfig.IsEnabled(SentryConfig.PlaceholderDsn.ToLowerInvariant()));
    }

    [Fact]
    public void DsnRealDistintoDelPlaceholder_SentryQueda_Habilitado()
    {
        Assert.True(SentryConfig.IsEnabled("https://examplePublicKey@o0.ingest.sentry.io/0"));
    }
}
