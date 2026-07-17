namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Nodo mínimo de proyecto para razonar sobre la jerarquía padre-hijo (campo
/// <c>parent_project_id</c> del contrato de sync), independiente de <c>AudioProject</c> y sin
/// tocar disco.
/// </summary>
public sealed record ProjectNode(string Id, string Name, string? ParentProjectId);

/// <summary>
/// Lógica pura sobre la jerarquía de proyectos: resolver la ruta relativa de cada proyecto en
/// el árbol (para cuando se ancle a carpetas anidadas en el filesystem) y ordenar un batch de
/// push para que los padres se manden antes que sus hijos (contrato: "si mandás jerarquía
/// nueva, mandá los upserts padre-primero"). Sin I/O, no arma ni toca <c>AudioProject</c>.
/// </summary>
public static class ProjectHierarchy
{
    /// <summary>
    /// Resuelve, para cada proyecto de <paramref name="projects"/>, su ruta relativa en el
    /// árbol (p.ej. "Empresa/Área/Proyecto"), uniendo los nombres de sus ancestros con
    /// <paramref name="separator"/>. No depende del orden de <paramref name="projects"/> (busca
    /// por id, no in-order). Casos defensivos:
    /// <list type="bullet">
    /// <item>Sin padre (o <c>ParentProjectId</c> apunta a un id que no está en la lista):
    /// se trata como raíz, la ruta es solo su propio nombre.</item>
    /// <item>Ciclo en la cadena de ancestros (datos corruptos, p.ej. A padre de B y B padre de
    /// A): se corta apenas se detecta y ese proyecto cae a raíz (su propio nombre), en vez de
    /// colgarse o devolver una ruta parcial arbitraria según en qué nodo se detectó el ciclo.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveRelativePaths(
        IReadOnlyList<ProjectNode> projects, char separator = '/')
    {
        var byId = projects.ToDictionary(p => p.Id);
        var result = new Dictionary<string, string>(projects.Count);

        foreach (var project in projects)
            result[project.Id] = ResolvePath(project, byId, separator);

        return result;
    }

    private static string ResolvePath(ProjectNode start, Dictionary<string, ProjectNode> byId, char separator)
    {
        var chain = new List<string> { start.Name };
        var seen = new HashSet<string> { start.Id };

        var parentId = start.ParentProjectId;
        while (parentId is not null && byId.TryGetValue(parentId, out var parent))
        {
            if (!seen.Add(parentId))
            {
                // Ciclo: se descarta toda la cadena de ancestros (no solo el tramo circular)
                // para no depender de en qué nodo del ciclo se lo detectó. Raíz = propio nombre.
                return start.Name;
            }

            chain.Add(parent.Name);
            parentId = parent.ParentProjectId;
        }

        chain.Reverse();
        return string.Join(separator, chain);
    }

    /// <summary>
    /// Ordena <paramref name="projects"/> para un batch de push de forma que cada padre quede
    /// antes que sus hijos (orden topológico, Kahn). Un <c>ParentProjectId</c> que NO está en
    /// este mismo batch no es una dependencia (ya existe del lado remoto, no bloquea al hijo).
    /// Defensivo contra ciclos: los proyectos que quedan atrapados en un ciclo se agregan al
    /// final en su orden original en vez de colgarse o tirar una excepción — best-effort, un
    /// batch corrupto no debe bloquear el push completo.
    /// </summary>
    public static IReadOnlyList<string> OrderParentFirst(IReadOnlyList<ProjectNode> projects)
    {
        var ids = new HashSet<string>(projects.Select(p => p.Id));
        var childrenOf = ids.ToDictionary(id => id, _ => new List<string>());
        var inDegree = ids.ToDictionary(id => id, _ => 0);

        foreach (var project in projects)
        {
            if (project.ParentProjectId is not null && ids.Contains(project.ParentProjectId))
            {
                childrenOf[project.ParentProjectId].Add(project.Id);
                inDegree[project.Id]++;
            }
        }

        var queue = new Queue<string>(projects.Where(p => inDegree[p.Id] == 0).Select(p => p.Id));
        var result = new List<string>(projects.Count);
        var visited = new HashSet<string>();

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id))
                continue;

            result.Add(id);
            foreach (var child in childrenOf[id])
            {
                if (--inDegree[child] == 0)
                    queue.Enqueue(child);
            }
        }

        if (result.Count < projects.Count)
        {
            foreach (var project in projects)
                if (visited.Add(project.Id))
                    result.Add(project.Id);
        }

        return result;
    }
}
