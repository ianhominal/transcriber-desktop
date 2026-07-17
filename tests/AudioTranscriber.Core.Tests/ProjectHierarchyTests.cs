using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Lógica pura para reflejar la jerarquía padre-hijo de proyectos (campo <c>parent_project_id</c>
/// del contrato de sync nuevo): resolución de la ruta relativa de cada proyecto en el árbol
/// (para el día que se ancle a carpetas anidadas) y el orden padre-primero que debe llevar un
/// batch de push. Sin I/O: no toca disco, no arma <c>AudioProject</c>.
/// </summary>
public class ProjectHierarchyTests
{
    [Fact]
    public void ResolveRelativePaths_ProyectoSinPadre_EsRaiz()
    {
        var projects = new[] { new ProjectNode("p1", "Trabajo", null) };

        var paths = ProjectHierarchy.ResolveRelativePaths(projects);

        Assert.Equal("Trabajo", paths["p1"]);
    }

    [Fact]
    public void ResolveRelativePaths_TresNiveles_ArmaRutaCompleta()
    {
        var projects = new[]
        {
            new ProjectNode("abuelo", "Empresa", null),
            new ProjectNode("padre", "Área", "abuelo"),
            new ProjectNode("hijo", "Proyecto", "padre"),
        };

        var paths = ProjectHierarchy.ResolveRelativePaths(projects);

        Assert.Equal("Empresa", paths["abuelo"]);
        Assert.Equal("Empresa/Área", paths["padre"]);
        Assert.Equal("Empresa/Área/Proyecto", paths["hijo"]);
    }

    [Fact]
    public void ResolveRelativePaths_OrdenInvertido_ResuelveIgual()
    {
        // El padre llega DESPUÉS que el hijo en la lista (p.ej. orden de llegada del pull).
        var projects = new[]
        {
            new ProjectNode("hijo", "Proyecto", "padre"),
            new ProjectNode("padre", "Área", "abuelo"),
            new ProjectNode("abuelo", "Empresa", null),
        };

        var paths = ProjectHierarchy.ResolveRelativePaths(projects);

        Assert.Equal("Empresa/Área/Proyecto", paths["hijo"]);
    }

    [Fact]
    public void ResolveRelativePaths_Huerfano_ParentIdInexistente_TratadoComoRaiz()
    {
        var projects = new[]
        {
            new ProjectNode("p1", "Proyecto suelto", "no-existe-este-id"),
        };

        var paths = ProjectHierarchy.ResolveRelativePaths(projects);

        Assert.Equal("Proyecto suelto", paths["p1"]);
    }

    [Fact]
    public void ResolveRelativePaths_Ciclo_NoCicla_TrataAmbosComoRaiz()
    {
        // Datos corruptos: A es padre de B y B es padre de A. Defensivo: nunca debe colgarse,
        // y cada nodo cae a su propio nombre (raíz) en vez de una ruta arbitraria.
        var projects = new[]
        {
            new ProjectNode("a", "A", "b"),
            new ProjectNode("b", "B", "a"),
        };

        var paths = ProjectHierarchy.ResolveRelativePaths(projects);

        Assert.Equal("A", paths["a"]);
        Assert.Equal("B", paths["b"]);
    }

    [Fact]
    public void ResolveRelativePaths_CicloDeUnSoloNodo_SeAutoreferencia_TratadoComoRaiz()
    {
        var projects = new[] { new ProjectNode("a", "A", "a") };

        var paths = ProjectHierarchy.ResolveRelativePaths(projects);

        Assert.Equal("A", paths["a"]);
    }

    [Fact]
    public void OrderParentFirst_PadreQuedaAntesQueHijo()
    {
        var projects = new[]
        {
            new ProjectNode("hijo", "Proyecto", "padre"),
            new ProjectNode("padre", "Área", null),
        };

        var order = ProjectHierarchy.OrderParentFirst(projects);

        Assert.Equal(new[] { "padre", "hijo" }, order);
    }

    [Fact]
    public void OrderParentFirst_TresNiveles_QuedanEnOrdenDeAncestria()
    {
        var projects = new[]
        {
            new ProjectNode("nieto", "Proyecto", "hijo"),
            new ProjectNode("hijo", "Área", "abuelo"),
            new ProjectNode("abuelo", "Empresa", null),
        };

        var order = ProjectHierarchy.OrderParentFirst(projects);

        Assert.Equal(new[] { "abuelo", "hijo", "nieto" }, order);
    }

    [Fact]
    public void OrderParentFirst_SinJerarquia_MantieneOrdenOriginal()
    {
        var projects = new[]
        {
            new ProjectNode("p1", "Uno", null),
            new ProjectNode("p2", "Dos", null),
            new ProjectNode("p3", "Tres", null),
        };

        var order = ProjectHierarchy.OrderParentFirst(projects);

        Assert.Equal(new[] { "p1", "p2", "p3" }, order);
    }

    [Fact]
    public void OrderParentFirst_PadreFueraDelBatch_NoBloqueaAlHijo()
    {
        // El padre ya existe del lado remoto y no viene en este push: no es una dependencia
        // dentro del batch, así que el hijo se manda igual.
        var projects = new[] { new ProjectNode("hijo", "Proyecto", "padre-que-no-viene") };

        var order = ProjectHierarchy.OrderParentFirst(projects);

        Assert.Equal(new[] { "hijo" }, order);
    }

    [Fact]
    public void OrderParentFirst_Ciclo_NoCicla_DevuelveTodosLosItems()
    {
        var projects = new[]
        {
            new ProjectNode("a", "A", "b"),
            new ProjectNode("b", "B", "a"),
        };

        var order = ProjectHierarchy.OrderParentFirst(projects);

        Assert.Equal(2, order.Count);
        Assert.Contains("a", order);
        Assert.Contains("b", order);
    }
}
