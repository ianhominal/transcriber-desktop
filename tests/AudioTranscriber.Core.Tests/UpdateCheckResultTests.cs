using AudioTranscriber.Core.Updates;

namespace AudioTranscriber.Core.Tests;

public class UpdateCheckResultTests
{
    [Fact]
    public void UpToDate_GuardaLaVersionActual_YNoTieneError()
    {
        var result = UpdateCheckResult.UpToDate("1.0.9");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal("1.0.9", result.Version);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Available_GuardaLaVersionNueva_YNoTieneError()
    {
        var result = UpdateCheckResult.Available("1.0.10");

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("1.0.10", result.Version);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Error_GuardaElMensaje_YNoTieneVersion()
    {
        var result = UpdateCheckResult.Error("No se pudo verificar (revisá tu conexión).");

        Assert.Equal(UpdateCheckStatus.Error, result.Status);
        Assert.Null(result.Version);
        Assert.Equal("No se pudo verificar (revisá tu conexión).", result.ErrorMessage);
    }
}
