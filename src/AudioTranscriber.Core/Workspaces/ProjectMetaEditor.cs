namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Guarda título, descripción y color de un proyecto manteniendo la invariante del sistema:
/// <see cref="AudioProject.Name"/> (carpeta en disco) y <see cref="AudioProject.Title"/>
/// (metadata) SIEMPRE se mantienen alineados. Mismo criterio que ya aplica
/// <see cref="Sync.SyncEngine"/> al bajar un rename remoto (ver
/// <c>SyncEngine.ExecutePullProjectUpsert</c>): si el título cambia, la carpeta se renombra
/// también, nunca uno sin el otro.
///
/// Bug que esto reemplaza: la UI tenía DOS caminos separados para "editar un proyecto" -- uno
/// escribía solo <see cref="AudioProject.Name"/> (rename de carpeta, sin tocar Title) y otro
/// escribía solo <see cref="AudioProject.Title"/> (metadata, sin tocar la carpeta). Cualquiera de
/// los dos por separado desalinea la pareja Name/Title -- y esa desalineación importa de verdad:
/// <c>SyncEngine.ExecutePushProjectUpsert</c> sube <c>Title</c> como nombre del proyecto en la
/// nube, y <c>LocalScanner.ScanDetailed</c> hashea <c>Title</c> (no <c>Name</c>) para decidir si
/// hay cambios que sincronizar. Este helper es el único camino de escritura para que eso no vuelva
/// a pasar.
/// </summary>
public static class ProjectMetaEditor
{
    /// <summary>
    /// Resultado de <see cref="Apply"/>. <paramref name="NewFolderName"/> es el
    /// <see cref="AudioProject.Name"/> resultante (cambió si el título disparó un rename de
    /// carpeta) -- el caller lo necesita para re-seleccionar el proyecto en el árbol después de
    /// recargar desde disco.
    /// </summary>
    public readonly record struct Result(bool Success, string? ErrorMessage, string? NewFolderName)
    {
        public static Result Ok(string newFolderName) => new(true, null, newFolderName);
        public static Result Fail(string errorMessage) => new(false, errorMessage, null);
    }

    /// <summary>
    /// Aplica una edición de título/descripción/color a <paramref name="project"/>, renombrando su
    /// carpeta si (y solo si) el título cambió. Nunca tira: cualquier falla vuelve como
    /// <see cref="Result"/> con un mensaje en criollo, sin jerga técnica ni nombres de excepción.
    ///
    /// Orden elegido (importa para no dejar el sistema a medio camino ante una falla):
    /// <list type="number">
    /// <item>Si el título cambió: primero se intenta el rename de CARPETA
    /// (<see cref="Workspace.RenameProject"/>). Si falla (carpeta bloqueada, nombre ya usado,
    /// título que sanitiza a vacío) se corta ACÁ -- todavía no se escribió metadata nueva en
    /// ningún lado, así que el proyecto queda exactamente como estaba antes de abrir el diálogo.
    /// La alternativa (guardar metadata primero y renombrar después) es la que produce el bug que
    /// este helper reemplaza: si el rename fallara después de guardar el Title nuevo, la carpeta
    /// se quedaría con el nombre viejo mientras el Title ya apunta al nuevo -- la misma
    /// desalineación que se está arreglando.</item>
    /// <item>Recién ahí se guarda la metadata (Title/Description/Color) en la carpeta ya
    /// renombrada (o en la misma de siempre, si no hubo rename). Este paso es una escritura de
    /// archivo local chica e inmediatamente después de un rename que ya tuvo éxito -- el riesgo de
    /// que falle es bajo, pero si pasa (disco lleno, antivirus, etc.) se avisa explícitamente que
    /// el nombre de la carpeta SÍ cambió pero los datos no se guardaron, en vez de fingir que todo
    /// salió bien.</item>
    /// </list>
    /// Si el título NO cambió, nunca se llama a <see cref="Workspace.RenameProject"/> (evita un
    /// <c>Directory.Move</c> inútil cuando la usuaria solo tocó Descripción o Color).
    /// </summary>
    public static Result Apply(
        Workspace workspace, AudioProject project, string newTitle, string description, string? color)
    {
        if (project.IsGeneral)
        {
            // Defensa en profundidad: la UI ya excluye "General" de este flujo (CanEditProject),
            // pero Workspace.RenameProject tira a propósito si igual se lo invoca -- mejor un
            // mensaje claro acá que dejar escapar esa excepción.
            return Result.Fail("El proyecto General no se puede editar.");
        }

        var title = (newTitle ?? string.Empty).Trim();
        var desc = description ?? string.Empty;
        var titleChanged = !string.Equals(title, project.Title, StringComparison.Ordinal);

        var folderPath = project.FolderPath;
        var folderName = project.Name;

        if (titleChanged)
        {
            var safe = Workspace.Sanitize(title);
            if (string.IsNullOrWhiteSpace(safe))
                return Result.Fail("El título no puede quedar vacío.");

            // Si el título cambió pero sanitiza a la MISMA carpeta que ya existe (p.ej. solo se
            // agregó/sacó un emoji o un carácter inválido, ver Workspace.Sanitize) no hay carpeta
            // que mover: Directory.Move con origen y destino iguales no tiene nada que hacer acá,
            // así que se salta el rename y se va directo a guardar metadata.
            if (!string.Equals(safe, project.Name, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    workspace.RenameProject(project, title);
                }
                catch (Exception ex)
                {
                    return Result.Fail(FriendlyRenameError(ex));
                }

                folderName = safe;
                folderPath = Path.Combine(workspace.AudiosPath, safe);
            }
        }

        var target = new AudioProject
        {
            Name = folderName,
            FolderPath = folderPath,
            IsGeneral = false,
            Title = title,
            Description = desc,
            Color = color,
            Audios = Array.Empty<AudioItem>(),
        };

        try
        {
            workspace.SaveProjectMeta(target);
        }
        catch (Exception)
        {
            return titleChanged && !string.Equals(folderName, project.Name, StringComparison.OrdinalIgnoreCase)
                ? Result.Fail("Cambiamos el nombre de la carpeta, pero no se pudo guardar el título y la descripción. Probá de nuevo.")
                : Result.Fail("No se pudo guardar la información del proyecto. Probá de nuevo.");
        }

        return Result.Ok(folderName);
    }

    private static string FriendlyRenameError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "No tenemos permiso para renombrar la carpeta del proyecto.",
        IOException => "No se pudo renombrar la carpeta del proyecto. Puede estar en uso o ya existir otra con ese nombre.",
        ArgumentException => "El título no puede quedar vacío.",
        _ => "No se pudo cambiar el nombre del proyecto. Probá de nuevo.",
    };
}
