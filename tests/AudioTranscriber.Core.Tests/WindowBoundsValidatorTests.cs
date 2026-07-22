using AudioTranscriber.Core.Ui;

namespace AudioTranscriber.Core.Tests;

public class WindowBoundsValidatorTests
{
    private static readonly ScreenRect PrimaryScreen = new(0, 0, 1920, 1080);
    private static readonly ScreenRect Fallback = new(100, 100, 1000, 700);

    [Fact]
    public void Validate_devuelve_los_bounds_guardados_cuando_estan_completamente_dentro_de_un_monitor()
    {
        var saved = new ScreenRect(200, 200, 1000, 700);

        var result = WindowBoundsValidator.Validate(saved, new[] { PrimaryScreen }, Fallback);

        Assert.Equal(saved, result);
    }

    [Fact]
    public void Validate_cae_al_fallback_cuando_los_bounds_guardados_estan_completamente_fuera_de_pantalla()
    {
        // Monitor secundario desconectado: coordenadas que en su momento tenían sentido, hoy no
        // interseccionan ningún monitor conectado.
        var saved = new ScreenRect(3000, 200, 1000, 700);

        var result = WindowBoundsValidator.Validate(saved, new[] { PrimaryScreen }, Fallback);

        Assert.Equal(Fallback, result);
    }

    [Fact]
    public void Validate_cae_al_fallback_con_coordenadas_negativas_lejos_de_cualquier_monitor()
    {
        var saved = new ScreenRect(-5000, -5000, 1000, 700);

        var result = WindowBoundsValidator.Validate(saved, new[] { PrimaryScreen }, Fallback);

        Assert.Equal(Fallback, result);
    }

    [Fact]
    public void Validate_reusa_los_bounds_guardados_si_intersecan_lo_suficiente_aunque_no_esten_100_por_ciento_dentro()
    {
        // Sale 900px por la derecha del monitor de 1920 de ancho, pero sigue quedando una franja
        // grande y agarrable (title bar) adentro.
        var saved = new ScreenRect(1800, 200, 1000, 700);

        var result = WindowBoundsValidator.Validate(saved, new[] { PrimaryScreen }, Fallback);

        Assert.Equal(saved, result);
    }

    [Fact]
    public void Validate_cae_al_fallback_cuando_solo_asoma_un_sliver_insuficiente_para_agarrar_la_ventana()
    {
        // Solo 10x10 px de intersección con el monitor: por debajo del mínimo agarrable
        // (MinVisibleWidth/MinVisibleHeight).
        var saved = new ScreenRect(1910, 1070, 1000, 700);

        var result = WindowBoundsValidator.Validate(saved, new[] { PrimaryScreen }, Fallback);

        Assert.Equal(Fallback, result);
    }

    [Fact]
    public void Validate_reusa_los_bounds_guardados_en_un_monitor_secundario_todavia_conectado()
    {
        var secondaryScreen = new ScreenRect(1920, 0, 1920, 1080);
        var saved = new ScreenRect(2200, 300, 800, 600);

        var result = WindowBoundsValidator.Validate(saved, new[] { PrimaryScreen, secondaryScreen }, Fallback);

        Assert.Equal(saved, result);
    }

    [Fact]
    public void Validate_cae_al_fallback_cuando_no_hay_ningun_monitor_conectado()
    {
        var saved = new ScreenRect(200, 200, 1000, 700);

        var result = WindowBoundsValidator.Validate(saved, Array.Empty<ScreenRect>(), Fallback);

        Assert.Equal(Fallback, result);
    }
}
