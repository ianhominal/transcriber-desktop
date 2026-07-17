using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.Tests;

public class ProjectTests : IDisposable
{
    private readonly string _root;

    public ProjectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "at_proj_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ListProjects_always_has_General_first()
    {
        var ws = Workspace.OpenOrCreate(_root);

        var projects = ws.ListProjects();

        Assert.NotEmpty(projects);
        Assert.True(projects[0].IsGeneral);
        Assert.Equal("General", projects[0].Name);
    }

    [Fact]
    public void CreateProject_creates_subfolder_and_shows_up()
    {
        var ws = Workspace.OpenOrCreate(_root);

        var created = ws.CreateProject("Reunión de equipo");

        Assert.True(Directory.Exists(created.FolderPath));
        var names = ws.ListProjects().Select(p => p.Name).ToList();
        Assert.Contains("Reunión de equipo", names);
    }

    [Fact]
    public void Audio_in_project_maps_transcript_into_project_subfolder()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Proyecto A");
        File.WriteAllText(Path.Combine(proj.FolderPath, "nota.mp3"), "");

        var reloaded = ws.ListProjects().Single(p => p.Name == "Proyecto A");
        var audio = reloaded.Audios.Single();

        Assert.Equal("nota.mp3", audio.FileName);
        Assert.Equal(Path.Combine(ws.TranscriptsPath, "Proyecto A", "nota.txt"), audio.TranscriptPath);
    }

    [Fact]
    public void MoveAudio_moves_file_and_transcript()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var dest = ws.CreateProject("Destino");
        // audio suelto en General, con transcript
        File.WriteAllText(Path.Combine(ws.AudiosPath, "charla.mp3"), "");
        File.WriteAllText(ws.TranscriptPathFor("charla.mp3"), "texto");

        var general = ws.ListProjects().Single(p => p.IsGeneral);
        var audio = general.Audios.Single(a => a.FileName == "charla.mp3");

        ws.MoveAudio(audio, dest);

        Assert.True(File.Exists(Path.Combine(dest.FolderPath, "charla.mp3")));
        Assert.False(File.Exists(Path.Combine(ws.AudiosPath, "charla.mp3")));
        Assert.True(File.Exists(Path.Combine(ws.TranscriptsPath, "Destino", "charla.txt")));
    }

    [Fact]
    public void SaveProjectMeta_persists_title_and_description()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Con meta");
        proj.Title = "Mi título lindo";
        proj.Description = "Audios de la clase de历史";

        ws.SaveProjectMeta(proj);

        var reloaded = ws.ListProjects().Single(p => p.Name == "Con meta");
        Assert.Equal("Mi título lindo", reloaded.Title);
        Assert.Equal("Audios de la clase de历史", reloaded.Description);
    }

    [Fact]
    public void SaveProjectMeta_persists_color_roundtrip()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Con color");
        proj.Color = "indigo";

        ws.SaveProjectMeta(proj);

        var reloaded = ws.ListProjects().Single(p => p.Name == "Con color");
        Assert.Equal("indigo", reloaded.Color);
    }

    [Fact]
    public void SaveProjectMeta_color_null_persiste_como_sin_color()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Sin color explicito");
        proj.Color = null;

        ws.SaveProjectMeta(proj);

        var reloaded = ws.ListProjects().Single(p => p.Name == "Sin color explicito");
        Assert.Null(reloaded.Color);
    }

    [Fact]
    public void SaveProjectMeta_id_de_color_invalido_se_normaliza_a_null_antes_de_persistir()
    {
        // Defensa en profundidad: si por algún motivo llega un id que no está en la paleta (nunca
        // debería, la UI solo ofrece los 12 ids válidos), SaveProjectMeta no lo persiste tal cual.
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Color invalido");
        proj.Color = "magenta"; // no existe en ProjectColorPalette

        ws.SaveProjectMeta(proj);

        var reloaded = ws.ListProjects().Single(p => p.Name == "Color invalido");
        Assert.Null(reloaded.Color);
    }

    [Fact]
    public void ReadMeta_json_viejo_sin_campo_color_no_rompe_y_deja_Color_null()
    {
        // Backward compat: un _proyecto.json escrito ANTES de esta feature no tiene la propiedad
        // "color" -- System.Text.Json debe dejarla en su default (null) en vez de tirar.
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Proyecto viejo");
        File.WriteAllText(
            Path.Combine(proj.FolderPath, "_proyecto.json"),
            """{"Title":"Proyecto viejo","Description":"sin campo color"}""");

        var reloaded = ws.ListProjects().Single(p => p.Name == "Proyecto viejo");

        Assert.Equal("sin campo color", reloaded.Description);
        Assert.Null(reloaded.Color);
    }

    [Fact]
    public void ReadMeta_color_invalido_o_desconocido_en_el_json_cae_a_null_sin_tirar()
    {
        // El JSON en disco trae un id que ya no existe en la paleta (versión futura de la app, o
        // edición manual del archivo) -- debe degradar a "sin color", nunca tirar.
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Color futuro");
        File.WriteAllText(
            Path.Combine(proj.FolderPath, "_proyecto.json"),
            """{"Title":"Color futuro","Description":"","Color":"chartreuse-neon-9000"}""");

        var exception = Record.Exception(() => ws.ListProjects());

        Assert.Null(exception);
        var reloaded = ws.ListProjects().Single(p => p.Name == "Color futuro");
        Assert.Null(reloaded.Color);
    }

    [Fact]
    public void ReadMeta_json_completamente_corrupto_no_rompe_y_Color_queda_null()
    {
        // Ya cubierto para Title/Description en otro lugar del código; acá se fija explícitamente
        // que Color también degrada a null (no crashea) ante JSON corrupto.
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Json corrupto");
        File.WriteAllText(Path.Combine(proj.FolderPath, "_proyecto.json"), "{ esto no es json valido");

        var exception = Record.Exception(() => ws.ListProjects());

        Assert.Null(exception);
        var reloaded = ws.ListProjects().Single(p => p.Name == "Json corrupto");
        Assert.Null(reloaded.Color);
    }

    [Fact]
    public void CreateProject_nuevo_proyecto_arranca_sin_color()
    {
        var ws = Workspace.OpenOrCreate(_root);

        var proj = ws.CreateProject("Recien creado");

        Assert.Null(proj.Color);
    }

    [Fact]
    public void General_nunca_tiene_color()
    {
        var ws = Workspace.OpenOrCreate(_root);

        var general = ws.ListProjects().Single(p => p.IsGeneral);

        Assert.Null(general.Color);
    }

    // ---- ReadProjectColor / preservación de color en pull-upsert ------------------------------
    // Bug real (confirmado en vivo, v1.0.23): SyncEngine.ExecutePullProjectUpsert reconstruye el
    // proyecto con Workspace.CreateProject (Color siempre null) y lo persiste con
    // SaveProjectMeta, así que CADA pull-upsert de un proyecto ya local (auto-sync cada 60s, o
    // "Sincronizar ahora") borraba en silencio el color que el usuario había elegido -- el server
    // no conoce este campo, no viaja en el DTO de sync. Ver changelog 2026-07-09.

    [Fact]
    public void ReadProjectColor_devuelve_el_color_persistido()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Con color");
        proj.Color = "indigo";
        ws.SaveProjectMeta(proj);

        var color = ws.ReadProjectColor(proj.FolderPath);

        Assert.Equal("indigo", color);
    }

    [Fact]
    public void ReadProjectColor_sin_campo_color_en_el_json_devuelve_null()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Sin campo color");
        File.WriteAllText(
            Path.Combine(proj.FolderPath, "_proyecto.json"),
            """{"Title":"Sin campo color","Description":""}""");

        Assert.Null(ws.ReadProjectColor(proj.FolderPath));
    }

    [Fact]
    public void ReadProjectColor_sin_proyecto_json_devuelve_null()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Sin _proyecto.json");

        Assert.Null(ws.ReadProjectColor(proj.FolderPath));
    }

    [Fact]
    public void ReadProjectColor_json_corrupto_devuelve_null_sin_tirar()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Json corrupto para color");
        File.WriteAllText(Path.Combine(proj.FolderPath, "_proyecto.json"), "{ esto no es json valido");

        var exception = Record.Exception(() => ws.ReadProjectColor(proj.FolderPath));

        Assert.Null(exception);
        Assert.Null(ws.ReadProjectColor(proj.FolderPath));
    }

    [Fact]
    public void PullUpsert_SinPreservarColor_LoBorraria_RegresionDelBugOriginal()
    {
        // Reproduce el bug TAL CUAL lo hacía el código viejo, para dejar constancia del "antes":
        // un proyecto ya local con color "indigo" en disco, seguido de exactamente lo que hacía
        // ExecutePullProjectUpsert ANTES del fix (CreateProject + SaveProjectMeta, sin preservar
        // color). Confirma que sin la línea de preservación el color se pierde.
        var ws = Workspace.OpenOrCreate(_root);
        var original = ws.CreateProject("grabado");
        original.Color = "indigo";
        ws.SaveProjectMeta(original);
        Assert.Equal("indigo", ws.ReadProjectColor(original.FolderPath)); // precondición

        // Camino viejo (buggy): un pull-upsert reconstruye el proyecto desde cero.
        var rebuilt = ws.CreateProject("grabado");
        rebuilt.Title = "grabado";
        rebuilt.Description = "";
        ws.SaveProjectMeta(rebuilt); // sin asignar rebuilt.Color -> queda null -> pisa el indigo

        Assert.Null(ws.ReadProjectColor(rebuilt.FolderPath));
    }

    [Fact]
    public void PullUpsert_ConPreservarColor_MantieneElColorLocalEnDisco()
    {
        // Mismo escenario que el test anterior, pero aplicando el patrón del fix real de
        // SyncEngine.ExecutePullProjectUpsert: leer el color ya persistido ANTES de
        // SaveProjectMeta y reasignarlo al proyecto recién reconstruido. Esto es lo que hoy hace
        // SyncEngine.cs (ws.ReadProjectColor(project.FolderPath) antes de SaveProjectMeta) -- acá
        // se fija el contrato de Workspace del que depende ese fix.
        var ws = Workspace.OpenOrCreate(_root);
        var original = ws.CreateProject("grabado");
        original.Color = "indigo";
        ws.SaveProjectMeta(original);

        // Pull-upsert reconstruye el proyecto (como CreateProject siempre hace, Color arranca null)...
        var rebuilt = ws.CreateProject("grabado");
        rebuilt.Title = "grabado";
        rebuilt.Description = "";
        Assert.Null(rebuilt.Color); // confirma la precondición del bug: arranca sin color

        // ...pero el fix preserva el color local antes de persistir.
        rebuilt.Color = ws.ReadProjectColor(rebuilt.FolderPath);
        ws.SaveProjectMeta(rebuilt);

        Assert.Equal("indigo", ws.ReadProjectColor(rebuilt.FolderPath));
        var reloaded = ws.ListProjects().Single(p => p.Name == "grabado");
        Assert.Equal("indigo", reloaded.Color);
    }

    [Fact]
    public void RenameProject_moves_the_folder()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Viejo");

        ws.RenameProject(proj, "Nuevo");

        var names = ws.ListProjects().Select(p => p.Name).ToList();
        Assert.Contains("Nuevo", names);
        Assert.DoesNotContain("Viejo", names);
    }

    [Fact]
    public void DeleteProject_removes_it()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var proj = ws.CreateProject("Borrable");

        ws.DeleteProject(proj);

        Assert.False(Directory.Exists(proj.FolderPath));
        Assert.DoesNotContain("Borrable", ws.ListProjects().Select(p => p.Name));
    }

    [Fact]
    public void Cannot_rename_or_delete_General()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var general = ws.ListProjects().Single(p => p.IsGeneral);

        Assert.Throws<InvalidOperationException>(() => ws.RenameProject(general, "x"));
        Assert.Throws<InvalidOperationException>(() => ws.DeleteProject(general));
    }
}
