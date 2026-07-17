using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncSessionGuardTests
{
    [Fact]
    public void MismoRefreshToken_NoFueSuperado_EsLogoutReal()
    {
        var result = SyncSessionGuard.WasSupersededByNewerLogin("token-abc", "token-abc");

        Assert.False(result);
    }

    [Fact]
    public void RefreshTokenActualDistinto_FueSuperadoPorLoginNuevo()
    {
        var result = SyncSessionGuard.WasSupersededByNewerLogin("token-nuevo", "token-viejo");

        Assert.True(result);
    }

    [Fact]
    public void SinSesionActual_NoFueSuperado_EsLogoutReal()
    {
        // Si ya no queda NINGÚN refresh token guardado, no hay "sesión nueva" que proteger:
        // es un logout real (o nunca hubo sesión).
        var result = SyncSessionGuard.WasSupersededByNewerLogin(string.Empty, "token-viejo");

        Assert.False(result);
    }

    [Fact]
    public void SinSesionActual_Null_NoFueSuperado()
    {
        var result = SyncSessionGuard.WasSupersededByNewerLogin(null, "token-viejo");

        Assert.False(result);
    }
}
