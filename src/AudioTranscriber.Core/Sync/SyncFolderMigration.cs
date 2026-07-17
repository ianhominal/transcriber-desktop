namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Resuelve la carpeta única de workspace/sync a partir de un <c>settings.json</c> viejo, que
/// tenía dos conceptos separados: <c>WorkspacePath</c> (carpeta que abría MainWindow) y
/// <c>SyncFolderPath</c> (carpeta elegida en la ventana de sync). Se unifican en un solo campo
/// (<c>SyncFolder</c>): la carpeta ES el workspace Y la que se sincroniza, estilo Dropbox.
/// Lógica pura (sin tocar disco) para poder testearla sin AppSettings ni WPF.
/// </summary>
public static class SyncFolderMigration
{
    /// <summary>
    /// Devuelve la carpeta a usar: si <paramref name="syncFolder"/> (el campo nuevo) ya tiene
    /// valor, gana. Si no, se migra desde <paramref name="legacySyncFolderPath"/> (la carpeta que
    /// ya se estaba sincronizando, más importante que solo un workspace de lectura). Si tampoco
    /// hay eso, se cae a <paramref name="legacyWorkspacePath"/>. Sin ninguno de los tres, vacío
    /// (no configurado).
    /// </summary>
    public static string Resolve(string? syncFolder, string? legacySyncFolderPath, string? legacyWorkspacePath)
    {
        if (!string.IsNullOrWhiteSpace(syncFolder))
            return syncFolder;
        if (!string.IsNullOrWhiteSpace(legacySyncFolderPath))
            return legacySyncFolderPath;
        return legacyWorkspacePath ?? string.Empty;
    }
}
