namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Resuelve un conflicto reportado por el push (<c>results[]</c> con <c>status: "conflict"</c>,
/// ADR-07c/d): el servidor ganó (su <c>version</c> avanzó más de lo que el cliente creía), pero la
/// edición local NO se pierde -- se preserva como archivo hermano y la copia remota (ya
/// disponible en el payload del pull de ESTE MISMO ciclo, ver <see cref="SyncEngine.RunAsync"/>)
/// queda en la ruta canónica. Nunca hace una llamada de red (toda la info remota la trae el
/// caller, ya bajada); sí hace I/O de disco (escribir los dos archivos), igual que el resto de
/// <see cref="SyncEngine"/>.
/// </summary>
public sealed class ConflictResolver
{
    /// <summary>Version del servidor a adoptar en la baseline tras resolver el conflicto.</summary>
    public sealed record Resolution(string Id, int AdoptedVersion);

    /// <summary>
    /// Escribe <paramref name="remoteText"/> en <paramref name="canonicalTranscriptPath"/> (la
    /// ruta que ya usa el resto del sync para esta transcripción). Si YA había contenido local en
    /// esa ruta, lo preserva primero como archivo hermano
    /// <c>{nombre}.conflicto-{yyyyMMddHHmmss}.txt</c>, junto a la ruta canónica -- cero pérdida,
    /// nunca se pisa en silencio una nota con cambios locales (ADR-07d). Sin contenido local
    /// previo (transcripción nueva que nunca se materializó en disco), no hay nada que preservar:
    /// solo se escribe la canónica.
    /// </summary>
    public Resolution Resolve(
        string id, int serverVersion, string canonicalTranscriptPath, string remoteText, DateTimeOffset now)
    {
        if (File.Exists(canonicalTranscriptPath))
        {
            var localText = File.ReadAllText(canonicalTranscriptPath);
            var conflictPath = BuildConflictSiblingPath(canonicalTranscriptPath, now);
            File.WriteAllText(conflictPath, localText);
        }

        var dir = Path.GetDirectoryName(canonicalTranscriptPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(canonicalTranscriptPath, remoteText);

        return new Resolution(id, serverVersion);
    }

    private static string BuildConflictSiblingPath(string canonicalTranscriptPath, DateTimeOffset now)
    {
        var dir = Path.GetDirectoryName(canonicalTranscriptPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(canonicalTranscriptPath);
        var stamp = now.ToString("yyyyMMddHHmmss");
        return Path.Combine(dir, $"{name}.conflicto-{stamp}.txt");
    }
}
