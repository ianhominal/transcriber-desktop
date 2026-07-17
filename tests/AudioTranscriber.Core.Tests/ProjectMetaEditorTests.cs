using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Cubre la invariante que <see cref="ProjectMetaEditor"/> existe para proteger: el
/// <see cref="AudioProject.Name"/> (carpeta en disco) y el <see cref="AudioProject.Title"/>
/// (metadata) se escriben SIEMPRE juntos, nunca uno sin el otro. Antes había dos caminos de UI
/// separados que desalineaban la pareja y hacían que la app mostrara/sincronizara un nombre
/// distinto del real.
/// </summary>
public class ProjectMetaEditorTests : IDisposable
{
    private readonly string _root;

    public ProjectMetaEditorTests()
    {
        // Carpeta temporal aislada por test-run (mismo patrón que WorkspaceTests).
        _root = Path.Combine(Path.GetTempPath(), "at_tests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // Relee el proyecto desde disco (como hace la UI real vía ListProjects), así los asserts
    // verifican lo que quedó PERSISTIDO, no lo que devolvió el helper en memoria.
    private static AudioProject Project(Workspace ws, string name) =>
        ws.ListProjects().First(p => !p.IsGeneral && p.Name == name);

    [Fact]
    public void Apply_saves_metadata_without_renaming_when_title_unchanged()
    {
        var ws = Workspace.OpenOrCreate(_root);
        ws.CreateProject("grabado");
        var project = Project(ws, "grabado");

        var result = ProjectMetaEditor.Apply(ws, project, "grabado", "mis notas", null);

        Assert.True(result.Success);
        Assert.Equal("grabado", result.NewFolderName);
        // No hubo rename: la misma carpeta de siempre sigue ahí.
        Assert.True(Directory.Exists(Path.Combine(ws.AudiosPath, "grabado")));

        var saved = Project(ws, "grabado");
        Assert.Equal("grabado", saved.Title);
        Assert.Equal("mis notas", saved.Description);
    }

    [Fact]
    public void Apply_renames_folder_and_saves_title_together_when_title_changes()
    {
        var ws = Workspace.OpenOrCreate(_root);
        ws.CreateProject("grabado");
        var project = Project(ws, "grabado");

        var result = ProjectMetaEditor.Apply(ws, project, "Reunion con Ana", "", null);

        Assert.True(result.Success);
        Assert.Equal("Reunion con Ana", result.NewFolderName);
        // La carpeta vieja ya no existe y la nueva sí: el Name (carpeta) siguió al Title.
        Assert.False(Directory.Exists(Path.Combine(ws.AudiosPath, "grabado")));
        Assert.True(Directory.Exists(Path.Combine(ws.AudiosPath, "Reunion con Ana")));

        var saved = Project(ws, "Reunion con Ana");
        Assert.Equal("Reunion con Ana", saved.Title);
    }

    [Fact]
    public void Apply_keeps_raw_title_while_folder_uses_sanitized_name()
    {
        // El corazón del asunto: Name (carpeta) y Title (metadata) PUEDEN diferir cuando el título
        // tiene caracteres inválidos para una carpeta -- pero se escriben juntos, en una sola
        // operación. La carpeta usa la versión sanitizada; el Title conserva el texto tal cual.
        var ws = Workspace.OpenOrCreate(_root);
        ws.CreateProject("grabado");
        var project = Project(ws, "grabado");

        var result = ProjectMetaEditor.Apply(ws, project, "Reunion: Ana", "", null);

        Assert.True(result.Success);
        var expectedFolder = Workspace.Sanitize("Reunion: Ana"); // ':' -> espacio
        Assert.NotEqual("Reunion: Ana", expectedFolder);
        Assert.Equal(expectedFolder, result.NewFolderName);
        Assert.True(Directory.Exists(Path.Combine(ws.AudiosPath, expectedFolder)));

        var saved = Project(ws, expectedFolder);
        Assert.Equal("Reunion: Ana", saved.Title); // el Title conserva el texto crudo, con el ':'
    }

    [Fact]
    public void Apply_fails_without_side_effects_when_title_is_empty()
    {
        var ws = Workspace.OpenOrCreate(_root);
        ws.CreateProject("grabado");
        var project = Project(ws, "grabado");

        var result = ProjectMetaEditor.Apply(ws, project, "   ", "no deberia guardarse", null);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        // Nada quedó a medias: la carpeta original sigue intacta y sin la descripción rechazada.
        Assert.True(Directory.Exists(Path.Combine(ws.AudiosPath, "grabado")));
        var untouched = Project(ws, "grabado");
        Assert.Equal("grabado", untouched.Title);
        Assert.Equal("", untouched.Description);
    }

    [Fact]
    public void Apply_rejects_the_general_project()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var general = ws.ListProjects().First(p => p.IsGeneral);

        var result = ProjectMetaEditor.Apply(ws, general, "Otro nombre", "", null);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
