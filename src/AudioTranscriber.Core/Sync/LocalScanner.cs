using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.Sync;

/// <summary>Datos de un proyecto local relevado por <see cref="LocalScanner"/>.</summary>
public sealed record LocalProjectEntry(
    string PathKey,
    string Id,
    string Name,
    string Title,
    string Description,
    string FolderPath,
    SyncItemState State);

/// <summary>Datos de una transcripción local relevada por <see cref="LocalScanner"/>.</summary>
public sealed record LocalTranscriptionEntry(
    string PathKey,
    string Id,
    string? ProjectId,
    string? ProjectName,
    string AudioFileName,
    string AudioPath,
    string TranscriptPath,
    bool HasLocalTranscript,
    string Text,
    SyncItemState State);

/// <summary>
/// Resultado completo de un relevamiento local: el mapa liviano que consume
/// <see cref="SyncPlanner"/> más el detalle (rutas, texto) que necesita <see cref="SyncEngine"/>
/// para ejecutar las acciones.
/// </summary>
public sealed class LocalSnapshot
{
    public required IReadOnlyDictionary<string, SyncItemState> Items { get; init; }
    public required IReadOnlyDictionary<string, LocalProjectEntry> Projects { get; init; }
    public required IReadOnlyDictionary<string, LocalTranscriptionEntry> Transcriptions { get; init; }
}

/// <summary>
/// Releva una carpeta de workspace (ver <see cref="Workspace"/>: audios/ con subcarpetas =
/// proyectos, transcripts/ espejando esa estructura) y arma el snapshot local para reconciliar
/// contra baseline/remoto. Un audio SIN su .txt todavía cuenta como transcripción sincronizable
/// (dispara el flujo "sube -&gt; el backend transcribe" del diseño), solo que sin texto.
/// </summary>
public sealed class LocalScanner
{
    /// <summary>Mapa liviano (id -&gt; estado) para pasarle a <see cref="SyncPlanner"/>.</summary>
    public Dictionary<string, SyncItemState> Scan(string rootPath) =>
        ScanDetailed(rootPath).Items.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>
    /// Releva la carpeta con detalle completo. <paramref name="idOverrides"/> (path-key -&gt; id)
    /// permite resolver el id real de items que se originaron en un pull remoto en vez de
    /// generarles un id local nuevo (ver <see cref="SyncIndex.LoadIdMap"/>).
    /// </summary>
    public LocalSnapshot ScanDetailed(string rootPath, IReadOnlyDictionary<string, string>? idOverrides = null)
    {
        idOverrides ??= new Dictionary<string, string>();
        var ws = Workspace.OpenOrCreate(rootPath);

        var items = new Dictionary<string, SyncItemState>();
        var projects = new Dictionary<string, LocalProjectEntry>();
        var transcriptions = new Dictionary<string, LocalTranscriptionEntry>();

        foreach (var project in ws.ListProjects())
        {
            string? projectId = null;
            if (!project.IsGeneral)
            {
                var pathKey = ProjectPathKey(project.Name);
                projectId = idOverrides.TryGetValue(pathKey, out var overriddenId) ? overriddenId : HashId(pathKey);

                var hash = ContentHasher.Hash(project.Title, project.Description);
                var updatedAt = new DateTimeOffset(Directory.GetLastWriteTimeUtc(project.FolderPath), TimeSpan.Zero);
                var state = new SyncItemState(projectId, SyncItemKind.Project, hash, updatedAt);

                items[projectId] = state;
                projects[projectId] = new LocalProjectEntry(
                    pathKey, projectId, project.Name, project.Title, project.Description, project.FolderPath, state);
            }

            foreach (var audio in project.Audios)
            {
                var trPathKey = TranscriptionPathKey(project.IsGeneral ? null : project.Name, audio.FileName);
                var hasOverride = idOverrides.TryGetValue(trPathKey, out var overriddenTrId);

                // Ítems SOLO TEXTO (audio.HasAudio == false, ver Workspace.ListAudiosIn) sin
                // override conocido: se EXCLUYEN del snapshot de sync (no de la UI -- la UI usa
                // Workspace.ListProjects() directo, sin pasar por acá). Este .txt huérfano puede
                // ser un audio que desapareció de disco por cualquier motivo ajeno al sync (mismo
                // caso que protege MergeWithLocalTombstones: "ausencia = sin cambios", NUNCA se
                // sintetiza una identidad nueva por inferencia). Si SÍ hay override -- lo puso
                // SyncEngine al bajar una transcripción sin audio_url_signed, ver
                // ExecutePullTranscriptionUpsertAsync -- el id es conocido y confiable, así que se
                // incluye con normalidad. Sin este freno, un .txt huérfano CUALQUIERA generaría un
                // id nuevo en cada scan y se pushearía como ítem local nuevo -- duplicando en el
                // servidor una transcripción que en realidad ya existía con otro id.
                if (!audio.HasAudio && !hasOverride)
                    continue;

                var trId = hasOverride ? overriddenTrId! : HashId(trPathKey);

                var hasTranscript = audio.HasTranscript;
                var text = hasTranscript ? File.ReadAllText(audio.TranscriptPath) : string.Empty;

                // Transcripciones SOLO TEXTO (audio.HasAudio == false, ver Workspace.ListAudiosIn)
                // no tienen audio.FullPath real (queda ""): GetLastWriteTimeUtc de un path vacío
                // tira ArgumentException. DateTime.MinValue como base hace que abajo "gane" siempre
                // transcriptWrite (el único archivo real que existe para estos items).
                var audioWrite = audio.HasAudio ? File.GetLastWriteTimeUtc(audio.FullPath) : DateTime.MinValue;
                var transcriptWrite = hasTranscript ? File.GetLastWriteTimeUtc(audio.TranscriptPath) : audioWrite;
                var updatedAtUtc = transcriptWrite > audioWrite ? transcriptWrite : audioWrite;
                var updatedAt = new DateTimeOffset(updatedAtUtc, TimeSpan.Zero);

                var hash = ContentHasher.Hash(audio.FileName, text, updatedAt.ToString("o"));
                var state = new SyncItemState(trId, SyncItemKind.Transcription, hash, updatedAt);

                items[trId] = state;
                transcriptions[trId] = new LocalTranscriptionEntry(
                    trPathKey, trId, projectId, project.IsGeneral ? null : project.Name,
                    audio.FileName, audio.FullPath, audio.TranscriptPath, hasTranscript, text, state);
            }
        }

        return new LocalSnapshot { Items = items, Projects = projects, Transcriptions = transcriptions };
    }

    /// <summary>Clave estable de un proyecto, previa al hash (útil para el mapa de identidad).</summary>
    public static string ProjectPathKey(string projectName) => $"project:{projectName}";

    /// <summary>Clave estable de una transcripción, previa al hash.</summary>
    public static string TranscriptionPathKey(string? projectName, string audioFileName) =>
        $"transcription:{projectName}/{audioFileName}";

    /// <summary>
    /// Resuelve el mismo id que <see cref="ScanDetailed"/> le asignaría a la transcripción
    /// (proyecto, archivo de audio) sin tener que correr un scan completo del disco. Pensado para
    /// <see cref="SyncCoordinator.MarkAudioDeletedForSync"/> (bug #1, borrado local no propagado a
    /// la nube): en el momento del borrado hace falta resolver el id de sync de ESE audio puntual
    /// para registrar su tombstone, replicando EXACTO el criterio de <paramref name="idMap"/> +
    /// <see cref="HashId"/> que usa el scan real -- cualquier diferencia acá generaría un id
    /// distinto y el tombstone nunca calzaría con la baseline.
    /// </summary>
    public static string ResolveTranscriptionId(string? projectName, string audioFileName, IReadOnlyDictionary<string, string> idMap)
    {
        var key = TranscriptionPathKey(projectName, audioFileName);
        return idMap.TryGetValue(key, out var mapped) ? mapped : HashId(key);
    }

    /// <summary>
    /// Id determinístico (mismo pathKey -&gt; mismo id, siempre, sin persistir nada) con formato
    /// UUID válido. Antes se usaba directo el hex de 64 caracteres de <see cref="ContentHasher"/>,
    /// que NO es un UUID: la columna "projects.id"/"transcriptions.id" del backend es `uuid` en
    /// Postgres, así que un push con ese id crudo fallaba el upsert (causa raíz del 500 al subir
    /// un proyecto/transcripción nuevo creado localmente). Se toman los primeros 16 bytes del
    /// hash SHA-256 del pathKey y se formatean como UUID (versión/variante seteadas para que sea
    /// un v4 sintácticamente válido); sigue siendo puro y determinístico, no hace falta tocar el
    /// id-map de <see cref="SyncIndex"/> para que sea estable entre ciclos.
    /// </summary>
    private static string HashId(string pathKey)
    {
        var hashHex = ContentHasher.Hash(pathKey);
        var bytes = Convert.FromHexString(hashHex[..32]);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40); // version 4
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant RFC 4122
        return new Guid(bytes).ToString();
    }
}
