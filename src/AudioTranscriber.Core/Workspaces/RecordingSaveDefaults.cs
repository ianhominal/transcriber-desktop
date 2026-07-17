namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Lógica pura para el diálogo de "guardar grabación" (proyecto + título) que se muestra al
/// terminar de grabar desde el micrófono. No toca disco: solo resuelve valores por defecto y
/// decide si hace falta mover/renombrar el audio ya grabado con el nombre automático. La I/O real
/// (mover/renombrar el archivo) sigue viviendo en <see cref="Workspace.MoveAudio"/> y
/// <see cref="Workspace.RenameAudio"/>, que <c>MainViewModel</c> llama solo cuando estos métodos
/// dicen que hace falta.
/// </summary>
public static class RecordingSaveDefaults
{
    /// <summary>Título prellenado en el diálogo: el nombre automático de la grabación, sin extensión.</summary>
    public static string DefaultTitle(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    /// <summary>
    /// Proyecto a preseleccionar en el diálogo: el proyecto actualmente enfocado en la app, si
    /// sigue existiendo entre las opciones disponibles; si no hay ninguno enfocado (o el que
    /// estaba ya no existe), cae al proyecto General (<c>null</c>).
    /// </summary>
    public static string? ResolveDefaultProject(
        IReadOnlyList<string> availableProjectNames, string? currentlySelectedProjectName)
    {
        if (currentlySelectedProjectName is null)
            return null;

        // Devuelve el nombre "canónico" tal como figura en availableProjectNames (no el que pasó
        // el caller): evita que una diferencia de mayúsculas termine usándose para crear una
        // carpeta nueva en vez de reusar el proyecto existente.
        return availableProjectNames.FirstOrDefault(
            n => string.Equals(n, currentlySelectedProjectName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True si el proyecto elegido en el diálogo es distinto de donde ya está el audio (así que
    /// hace falta <see cref="Workspace.MoveAudio"/>). <c>null</c> representa el proyecto General
    /// en ambos parámetros.
    /// </summary>
    public static bool NeedsMove(string? currentProjectName, string? chosenProjectName)
        => !string.Equals(currentProjectName, chosenProjectName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True si el título elegido en el diálogo (una vez sanitizado, mismo criterio que
    /// <see cref="Workspace.RenameAudio"/>) es distinto del nombre actual del archivo, así que
    /// hace falta renombrarlo. Comparación case-insensitive: solo cambiar mayúsculas/minúsculas no
    /// justifica un <c>File.Move</c> (evita el caso límite de origen/destino iguales salvo case).
    /// El caller es responsable de no invocar esto (ni <see cref="Workspace.RenameAudio"/>) con un
    /// título en blanco.
    /// </summary>
    public static bool NeedsRename(string currentTitle, string chosenTitle)
        => !string.Equals(
            (currentTitle ?? string.Empty).Trim(),
            Workspace.Sanitize(chosenTitle ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
}
