using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.Tests;

public class RecordingSaveDefaultsTests
{
    // ---- DefaultTitle -----------------------------------------------------

    [Fact]
    public void DefaultTitle_QuitaLaExtension()
    {
        var title = RecordingSaveDefaults.DefaultTitle("Grabación 2026-07-08 14-32-05.wav");

        Assert.Equal("Grabación 2026-07-08 14-32-05", title);
    }

    [Fact]
    public void DefaultTitle_SinExtension_DevuelveElNombreTalCual()
    {
        var title = RecordingSaveDefaults.DefaultTitle("SinExtension");

        Assert.Equal("SinExtension", title);
    }

    [Fact]
    public void DefaultTitle_ConVariosPuntos_SoloQuitaLaUltimaExtension()
    {
        var title = RecordingSaveDefaults.DefaultTitle("Reunión v2.final.wav");

        Assert.Equal("Reunión v2.final", title);
    }

    // ---- ResolveDefaultProject ---------------------------------------------

    [Fact]
    public void ResolveDefaultProject_SinProyectoSeleccionado_DevuelveNull_General()
    {
        var result = RecordingSaveDefaults.ResolveDefaultProject(
            new[] { "Trabajo", "Personal" }, currentlySelectedProjectName: null);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDefaultProject_ProyectoSeleccionadoExisteEnLaLista_LoPreseleccionaste()
    {
        var result = RecordingSaveDefaults.ResolveDefaultProject(
            new[] { "Trabajo", "Personal" }, currentlySelectedProjectName: "Personal");

        Assert.Equal("Personal", result);
    }

    [Fact]
    public void ResolveDefaultProject_ComparacionIgnoraMayusculas()
    {
        var result = RecordingSaveDefaults.ResolveDefaultProject(
            new[] { "Trabajo", "Personal" }, currentlySelectedProjectName: "PERSONAL");

        Assert.Equal("Personal", result);
    }

    [Fact]
    public void ResolveDefaultProject_ProyectoSeleccionadoYaNoExiste_CaeAGeneral()
    {
        var result = RecordingSaveDefaults.ResolveDefaultProject(
            new[] { "Trabajo" }, currentlySelectedProjectName: "Personal (borrado)");

        Assert.Null(result);
    }

    // ---- NeedsMove ----------------------------------------------------------

    [Fact]
    public void NeedsMove_AmbosGeneral_NoHaceFalta()
    {
        Assert.False(RecordingSaveDefaults.NeedsMove(currentProjectName: null, chosenProjectName: null));
    }

    [Fact]
    public void NeedsMove_MismoProyecto_NoHaceFalta()
    {
        Assert.False(RecordingSaveDefaults.NeedsMove(currentProjectName: "Trabajo", chosenProjectName: "Trabajo"));
    }

    [Fact]
    public void NeedsMove_MismoProyectoDistintaMayuscula_NoHaceFalta()
    {
        Assert.False(RecordingSaveDefaults.NeedsMove(currentProjectName: "Trabajo", chosenProjectName: "TRABAJO"));
    }

    [Fact]
    public void NeedsMove_DeGeneralAProyecto_HaceFalta()
    {
        Assert.True(RecordingSaveDefaults.NeedsMove(currentProjectName: null, chosenProjectName: "Trabajo"));
    }

    [Fact]
    public void NeedsMove_DeProyectoAGeneral_HaceFalta()
    {
        Assert.True(RecordingSaveDefaults.NeedsMove(currentProjectName: "Trabajo", chosenProjectName: null));
    }

    [Fact]
    public void NeedsMove_EntreDosProyectosDistintos_HaceFalta()
    {
        Assert.True(RecordingSaveDefaults.NeedsMove(currentProjectName: "Trabajo", chosenProjectName: "Personal"));
    }

    // ---- NeedsRename ----------------------------------------------------------

    [Fact]
    public void NeedsRename_MismoTitulo_NoHaceFalta()
    {
        Assert.False(RecordingSaveDefaults.NeedsRename("Grabación 2026-07-08 14-32-05", "Grabación 2026-07-08 14-32-05"));
    }

    [Fact]
    public void NeedsRename_TituloDistinto_HaceFalta()
    {
        Assert.True(RecordingSaveDefaults.NeedsRename("Grabación 2026-07-08 14-32-05", "Reunión de equipo"));
    }

    [Fact]
    public void NeedsRename_ElNuevoTituloSanitizadoQuedaIgualAlActual_NoHaceFalta()
    {
        // "/" es inválido en nombres de archivo: Workspace.Sanitize lo reemplaza por espacio,
        // igual que ya queda "Reunion Importante" -> no hace falta tocar el archivo.
        Assert.False(RecordingSaveDefaults.NeedsRename("Reunion Importante", "Reunion/Importante"));
    }

    [Fact]
    public void NeedsRename_SoloCambiaMayusculas_NoHaceFalta()
    {
        Assert.False(RecordingSaveDefaults.NeedsRename("Reunión", "REUNIÓN"));
    }

    [Fact]
    public void NeedsRename_TituloVacioContraUnoConTexto_HaceFalta()
    {
        // El caller es responsable de no llamar a RenameAudio con un título en blanco (mismo
        // criterio que ya valida Workspace.RenameAudio) — acá solo se documenta la comparación pura.
        Assert.True(RecordingSaveDefaults.NeedsRename("Grabación 2026-07-08 14-32-05", ""));
    }
}
