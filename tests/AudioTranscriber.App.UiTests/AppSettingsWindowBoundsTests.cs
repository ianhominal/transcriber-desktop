using System.Text.Json;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Cubre solo la forma en que los campos de "recordar tamaño/posición/estado de la ventana" viajan
/// por JSON (mismo criterio que <see cref="AppSettingsThemeTests"/>), sin tocar el archivo real ni
/// una Window de verdad -- la lógica de decisión (¿los bounds guardados siguen visibles? ¿qué
/// tamaño inicial usar?) vive en Core y está testeada en WindowBoundsValidatorTests/
/// InitialWindowSizerTests (AudioTranscriber.Core.Tests).
/// </summary>
public class AppSettingsWindowBoundsTests
{
    [Fact]
    public void Los_4_campos_de_bounds_son_null_por_default_primer_arranque()
    {
        var settings = new AppSettings();

        Assert.Null(settings.WindowWidth);
        Assert.Null(settings.WindowHeight);
        Assert.Null(settings.WindowLeft);
        Assert.Null(settings.WindowTop);
        Assert.Equal("Normal", settings.WindowState);
    }

    [Fact]
    public void Los_bounds_guardados_sobreviven_un_roundtrip_de_serializacion_JSON()
    {
        var settings = new AppSettings
        {
            WindowState = "Maximized",
            WindowLeft = 120.5,
            WindowTop = 80,
            WindowWidth = 1400.25,
            WindowHeight = 900,
        };

        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Maximized", deserialized!.WindowState);
        Assert.Equal(120.5, deserialized.WindowLeft);
        Assert.Equal(80, deserialized.WindowTop);
        Assert.Equal(1400.25, deserialized.WindowWidth);
        Assert.Equal(900, deserialized.WindowHeight);
    }

    [Fact]
    public void Bounds_ausentes_en_JSON_viejo_caen_a_null_no_a_una_excepcion()
    {
        // Simula un settings.json guardado ANTES de que existiera esta feature: el default de cada
        // property (null) cubre el campo faltante, sin migración explícita -- mismo criterio que
        // AppSettingsThemeTests.Theme_ausente_en_JSON_viejo_cae_al_default_System.
        var jsonSinBounds = "{\"Engine\":\"local\",\"Language\":\"es\"}";

        var deserialized = JsonSerializer.Deserialize<AppSettings>(jsonSinBounds);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.WindowWidth);
        Assert.Null(deserialized.WindowHeight);
        Assert.Null(deserialized.WindowLeft);
        Assert.Null(deserialized.WindowTop);
        Assert.Equal("Normal", deserialized.WindowState);
    }
}
