namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Decide si un cambio de archivo dentro de la carpeta sincronizada debe ignorarse para el
/// auto-sync: las carpetas internas del propio cliente (<c>.synccache</c> con el índice SQLite,
/// <c>.papelera</c> con los borrados movidos) nunca deberían disparar un ciclo de sync, sea quien
/// sea quien las toque. Lógica pura basada en rutas (sin tocar disco).
/// </summary>
public static class SyncWatchFilter
{
    private static readonly string[] IgnoredTopFolders = { ".synccache", ".papelera" };

    /// <summary>
    /// True si <paramref name="fullPath"/> (dentro de <paramref name="rootPath"/>) cae dentro de
    /// una de las carpetas internas ignoradas.
    /// </summary>
    public static bool ShouldIgnore(string rootPath, string fullPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath);
        if (string.IsNullOrEmpty(relative) || relative == ".")
            return false;

        var firstSegment = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            2)[0];

        foreach (var ignored in IgnoredTopFolders)
            if (string.Equals(firstSegment, ignored, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
