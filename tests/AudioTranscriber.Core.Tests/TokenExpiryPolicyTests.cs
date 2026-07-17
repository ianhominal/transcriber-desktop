using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class TokenExpiryPolicyTests
{
    [Fact]
    public void TokenLejosDeVencer_NoHaceFalta_Refrescar()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var expiresAt = now.AddHours(1).ToUnixTimeSeconds();

        Assert.False(TokenExpiryPolicy.ShouldRefresh(now, expiresAt));
    }

    [Fact]
    public void TokenYaVencido_HaceFalta_Refrescar()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var expiresAt = now.AddMinutes(-5).ToUnixTimeSeconds();

        Assert.True(TokenExpiryPolicy.ShouldRefresh(now, expiresAt));
    }

    [Fact]
    public void TokenDentroDelMargenDeSeguridad_HaceFalta_Refrescar()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        // Vence en 30s, margen por defecto es 60s -> ya está "por vencer".
        var expiresAt = now.AddSeconds(30).ToUnixTimeSeconds();

        Assert.True(TokenExpiryPolicy.ShouldRefresh(now, expiresAt));
    }

    [Fact]
    public void SinDatoDeVencimiento_HaceFalta_Refrescar()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

        Assert.True(TokenExpiryPolicy.ShouldRefresh(now, 0));
        Assert.True(TokenExpiryPolicy.ShouldRefresh(now, -1));
    }

    [Fact]
    public void MargenPersonalizado_SeRespeta()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var expiresAt = now.AddMinutes(10).ToUnixTimeSeconds();

        Assert.False(TokenExpiryPolicy.ShouldRefresh(now, expiresAt, TimeSpan.FromMinutes(5)));
        Assert.True(TokenExpiryPolicy.ShouldRefresh(now, expiresAt, TimeSpan.FromMinutes(15)));
    }
}
