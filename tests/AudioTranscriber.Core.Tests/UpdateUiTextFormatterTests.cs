using AudioTranscriber.Core.Updates;

namespace AudioTranscriber.Core.Tests;

public class UpdateUiTextFormatterTests
{
    // ---- FormatCurrentVersion ----------------------------------------------

    [Fact]
    public void FormatCurrentVersion_EtiquetaComoLaVersionEnEjecucion()
    {
        var text = UpdateUiTextFormatter.FormatCurrentVersion("1.0.10");

        Assert.Equal("Ejecutando v1.0.10", text);
    }

    // ---- FormatResult -------------------------------------------------------

    [Fact]
    public void FormatResult_UpToDate_MuestraQueYaEstaAlDia()
    {
        var text = UpdateUiTextFormatter.FormatResult(UpdateCheckResult.UpToDate("1.0.10"));

        Assert.Equal("Estás en la última versión (v1.0.10).", text);
    }

    [Fact]
    public void FormatResult_Available_MuestraLaVersionNuevaYaLista()
    {
        var text = UpdateUiTextFormatter.FormatResult(UpdateCheckResult.Available("1.0.11"));

        Assert.Equal("Hay una versión nueva (1.0.11). Ya está descargada y lista para instalar.", text);
    }

    [Fact]
    public void FormatResult_Error_DevuelveElMensajeDeError()
    {
        var text = UpdateUiTextFormatter.FormatResult(UpdateCheckResult.Error("No se pudo verificar (revisá tu conexión)."));

        Assert.Equal("No se pudo verificar (revisá tu conexión).", text);
    }

    [Fact]
    public void FormatResult_Error_SinMensaje_CaeAUnTextoGenerico()
    {
        var text = UpdateUiTextFormatter.FormatResult(new UpdateCheckResult(UpdateCheckStatus.Error, null, null));

        Assert.Equal("No se pudo verificar (revisá tu conexión).", text);
    }

    // ---- FormatBannerText -----------------------------------------------------

    [Fact]
    public void FormatBannerText_IncluyeLaVersionNueva()
    {
        var text = UpdateUiTextFormatter.FormatBannerText("1.0.11");

        Assert.Equal("Hay una actualización disponible (1.0.11).", text);
    }

    // ---- ShouldShowRestartButton ----------------------------------------------

    [Fact]
    public void ShouldShowRestartButton_ConResultadoAvailable_EsTrue()
    {
        Assert.True(UpdateUiTextFormatter.ShouldShowRestartButton(UpdateCheckResult.Available("1.0.11")));
    }

    [Fact]
    public void ShouldShowRestartButton_ConResultadoUpToDate_EsFalse()
    {
        Assert.False(UpdateUiTextFormatter.ShouldShowRestartButton(UpdateCheckResult.UpToDate("1.0.10")));
    }

    [Fact]
    public void ShouldShowRestartButton_ConResultadoError_EsFalse()
    {
        Assert.False(UpdateUiTextFormatter.ShouldShowRestartButton(UpdateCheckResult.Error("falló")));
    }

    [Fact]
    public void ShouldShowRestartButton_SinResultadoTodavia_EsFalse()
    {
        Assert.False(UpdateUiTextFormatter.ShouldShowRestartButton(null));
    }

    // ---- FormatPassiveStatus (estado pasivo en SettingsWindow, sin disparar un chequeo) --------

    [Fact]
    public void FormatPassiveStatus_SinResultadoTodavia_MuestraBuscando()
    {
        var text = UpdateUiTextFormatter.FormatPassiveStatus(null);

        Assert.Equal(UpdateUiTextFormatter.CheckingText, text);
    }

    [Fact]
    public void FormatPassiveStatus_UpToDate_MuestraAlDiaConVersion()
    {
        var text = UpdateUiTextFormatter.FormatPassiveStatus(UpdateCheckResult.UpToDate("1.0.10"));

        Assert.Equal("Al día (v1.0.10).", text);
    }

    [Fact]
    public void FormatPassiveStatus_Available_MuestraQueHayUnaVersionDisponible()
    {
        var text = UpdateUiTextFormatter.FormatPassiveStatus(UpdateCheckResult.Available("1.0.11"));

        Assert.Equal("Hay una actualización disponible: 1.0.11.", text);
    }

    [Fact]
    public void FormatPassiveStatus_Error_DevuelveElMensajeDeError()
    {
        var text = UpdateUiTextFormatter.FormatPassiveStatus(UpdateCheckResult.Error("No se pudo verificar (revisá tu conexión)."));

        Assert.Equal("No se pudo verificar (revisá tu conexión).", text);
    }

    [Fact]
    public void FormatPassiveStatus_Error_SinMensaje_CaeAUnTextoGenerico()
    {
        var text = UpdateUiTextFormatter.FormatPassiveStatus(new UpdateCheckResult(UpdateCheckStatus.Error, null, null));

        Assert.Equal("No se pudo verificar (revisá tu conexión).", text);
    }

    // ---- FormatDownloadingText (progreso real de descarga, ver UpdateService.DownloadProgressChanged) ----

    [Fact]
    public void FormatDownloadingText_ConTamanoConocido_MuestraPorcentajeYTamanoEnMb()
    {
        // 105 MB exactos (105 * 1024 * 1024) para que el redondeo no meta ruido en el assert.
        var text = UpdateUiTextFormatter.FormatDownloadingText(new UpdateProgress(45, 110_100_480));

        Assert.Equal("Descargando actualización — 45% de 105 MB.", text);
    }

    [Fact]
    public void FormatDownloadingText_SinTamanoConocido_MuestraSoloElPorcentaje()
    {
        var text = UpdateUiTextFormatter.FormatDownloadingText(new UpdateProgress(45, null));

        Assert.Equal("Descargando actualización — 45%.", text);
    }

    [Fact]
    public void FormatDownloadingText_ConTamanoCeroOInvalido_NoInventaUnTamano()
    {
        var text = UpdateUiTextFormatter.FormatDownloadingText(new UpdateProgress(10, 0));

        Assert.Equal("Descargando actualización — 10%.", text);
    }

    [Fact]
    public void FormatDownloadingText_RedondeaLosMegabytesAlEnteroMasCercano()
    {
        // 100.4 MB -> redondea a 100.
        var text = UpdateUiTextFormatter.FormatDownloadingText(new UpdateProgress(1, 105_295_923));

        Assert.Equal("Descargando actualización — 1% de 100 MB.", text);
    }

    // ---- ShouldShowDownloadProgress ----

    [Fact]
    public void ShouldShowDownloadProgress_ConProgresoActivo_EsTrue()
    {
        Assert.True(UpdateUiTextFormatter.ShouldShowDownloadProgress(new UpdateProgress(0, null)));
    }

    [Fact]
    public void ShouldShowDownloadProgress_SinProgreso_EsFalse()
    {
        Assert.False(UpdateUiTextFormatter.ShouldShowDownloadProgress(null));
    }
}
