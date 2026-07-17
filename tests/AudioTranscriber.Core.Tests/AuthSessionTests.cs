using System.Text.Json;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AuthSessionTests
{
    [Fact]
    public void ExpiresAtPresente_SeRespetaTalCual()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var session = new AuthSession { ExpiresAt = 1_003_600, ExpiresIn = 60 };

        session.EnsureExpiresAt(now);

        Assert.Equal(1_003_600, session.ExpiresAt);
    }

    [Fact]
    public void SoloExpiresIn_CalculaAbsolutoDesdeNow()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var session = new AuthSession { ExpiresAt = 0, ExpiresIn = 3600 };

        session.EnsureExpiresAt(now);

        Assert.Equal(1_003_600, session.ExpiresAt);
    }

    [Fact]
    public void SinExpiresAtNiExpiresIn_QuedaEnCero()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var session = new AuthSession { ExpiresAt = 0, ExpiresIn = null };

        session.EnsureExpiresAt(now);

        Assert.Equal(0, session.ExpiresAt);
    }

    [Fact]
    public void ExpiresInCero_NoSeUsa_QuedaEnCero()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var session = new AuthSession { ExpiresAt = 0, ExpiresIn = 0 };

        session.EnsureExpiresAt(now);

        Assert.Equal(0, session.ExpiresAt);
    }

    [Fact]
    public void ExpiresInNegativo_NoSeUsa_QuedaEnCero()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var session = new AuthSession { ExpiresAt = 0, ExpiresIn = -10 };

        session.EnsureExpiresAt(now);

        Assert.Equal(0, session.ExpiresAt);
    }

    [Fact]
    public void ExpiresAtInvalidoNegativo_SeIgnora_UsaExpiresIn()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var session = new AuthSession { ExpiresAt = -1, ExpiresIn = 3600 };

        session.EnsureExpiresAt(now);

        Assert.Equal(1_003_600, session.ExpiresAt);
    }

    [Fact]
    public void Deserializa_ExpiresAtYExpiresIn_AmbosPresentes()
    {
        var json = """
            {"access_token":"AT","refresh_token":"RT","expires_at":1699999999,"expires_in":3600,"user":{"id":"u1","email":"a@b.com"}}
            """;

        var session = JsonSerializer.Deserialize<AuthSession>(json)!;

        Assert.Equal(1699999999, session.ExpiresAt);
        Assert.Equal(3600, session.ExpiresIn);
    }

    [Fact]
    public void Deserializa_SoloExpiresIn_ExpiresAtQuedaEnCeroHastaEnsureExpiresAt()
    {
        // Respuesta real observada de Supabase Auth (/auth/v1/token): solo trae "expires_in",
        // no "expires_at". Este es exactamente el caso que causaba el bug: sin el fallback de
        // EnsureExpiresAt, ExpiresAt queda en 0.
        var json = """
            {"access_token":"AT","refresh_token":"RT","expires_in":3600,"user":{"id":"u1","email":"a@b.com"}}
            """;

        var session = JsonSerializer.Deserialize<AuthSession>(json)!;

        Assert.Equal(0, session.ExpiresAt);
        Assert.Equal(3600, session.ExpiresIn);
    }

    [Fact]
    public void Deserializa_SoloExpiresAt_ExpiresInQuedaNull()
    {
        var json = """
            {"access_token":"AT","refresh_token":"RT","expires_at":1699999999,"user":{"id":"u1","email":"a@b.com"}}
            """;

        var session = JsonSerializer.Deserialize<AuthSession>(json)!;

        Assert.Equal(1699999999, session.ExpiresAt);
        Assert.Null(session.ExpiresIn);
    }

    [Fact]
    public void Deserializa_SinNingunCampoDeVencimiento_QuedanEnDefault()
    {
        var json = """{"access_token":"AT","refresh_token":"RT","user":{"id":"u1","email":"a@b.com"}}""";

        var session = JsonSerializer.Deserialize<AuthSession>(json)!;

        Assert.Equal(0, session.ExpiresAt);
        Assert.Null(session.ExpiresIn);
    }
}
