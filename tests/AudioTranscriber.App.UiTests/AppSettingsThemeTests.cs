using System.Text.Json;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Cubre solo la forma en que <see cref="AppSettings.Theme"/> viaja por JSON (mismo mecanismo que
/// usa <see cref="AppSettings.Save"/>/<see cref="AppSettings.Load"/> contra
/// %LOCALAPPDATA%\AudioTranscriber\settings.json), sin tocar el archivo real: instanciar/serializar
/// a mano evita depender del filesystem o pisar el settings.json de quien corra los tests (mismo
/// criterio conservador que el resto de AppSettings, que no tiene tests de Save/Load contra disco).
/// </summary>
public class AppSettingsThemeTests
{
    [Fact]
    public void Theme_por_default_es_System()
    {
        var settings = new AppSettings();

        Assert.Equal("System", settings.Theme);
    }

    [Fact]
    public void Theme_sobrevive_un_roundtrip_de_serializacion_JSON()
    {
        var settings = new AppSettings { Theme = "Dark" };

        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Dark", deserialized!.Theme);
    }

    [Fact]
    public void Theme_ausente_en_JSON_viejo_cae_al_default_System()
    {
        // Simula un settings.json guardado ANTES de que existiera Theme (migración hacia adelante:
        // el default del property cubre el campo faltante, sin necesitar migración explícita como
        // SyncFolderMigration).
        var jsonSinTheme = "{\"Engine\":\"local\",\"Language\":\"es\"}";

        var deserialized = JsonSerializer.Deserialize<AppSettings>(jsonSinTheme);

        Assert.NotNull(deserialized);
        Assert.Equal("System", deserialized!.Theme);
    }
}
