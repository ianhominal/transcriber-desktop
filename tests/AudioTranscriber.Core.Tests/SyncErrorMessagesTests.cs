using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncErrorMessagesTests
{
    [Theory]
    [InlineData(SyncErrorCategory.NeedsLogin, "Iniciá sesión")]
    [InlineData(SyncErrorCategory.NetworkError, "Sin conexión")]
    [InlineData(SyncErrorCategory.ServerError, "Error del servidor, reintentando…")]
    [InlineData(SyncErrorCategory.Unknown, "Error de sincronización")]
    public void ChipFor_DevuelveLaEtiquetaCortaEsperadaPorCategoria(SyncErrorCategory category, string expected)
    {
        Assert.Equal(expected, SyncErrorMessages.ChipFor(category));
    }

    [Fact]
    public void StatusMessageFor_NeedsLogin_PideIniciarSesion()
    {
        var msg = SyncErrorMessages.StatusMessageFor(SyncErrorCategory.NeedsLogin, "irrelevante");

        Assert.Equal("Iniciá sesión para sincronizar.", msg);
    }

    [Fact]
    public void StatusMessageFor_NetworkError_AvisaSinConexion()
    {
        var msg = SyncErrorMessages.StatusMessageFor(SyncErrorCategory.NetworkError, "irrelevante");

        Assert.Contains("conexión", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusMessageFor_ServerError_AvisaReintento()
    {
        var msg = SyncErrorMessages.StatusMessageFor(SyncErrorCategory.ServerError, "irrelevante");

        Assert.Contains("servidor", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reintentando", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatusMessageFor_Unknown_IncluyeElMensajeDeLaExcepcionParaElDetalle()
    {
        var msg = SyncErrorMessages.StatusMessageFor(SyncErrorCategory.Unknown, "boom inesperado");

        Assert.Contains("boom inesperado", msg);
    }
}
