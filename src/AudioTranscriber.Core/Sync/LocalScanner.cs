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

    /// <summary>
    /// Pares path-key -&gt; id de los items que este scan tuvo que ACUÑAR de cero (sin entrada en
    /// <c>idOverrides</c>, ver <see cref="LocalScanner.ScanDetailed"/>). <see cref="SyncEngine"/> los
    /// mergea en su propio mapa de overrides INMEDIATAMENTE después del primer scan del ciclo (ADR-06.2/3
    /// del design) para que el id acuñado sobreviva al rescan y se persista aunque el push falle --
    /// re-acuñar en el ciclo siguiente duplicaría el item en el servidor.
    /// </summary>
    public required IReadOnlyDictionary<string, string> MintedIds { get; init; }
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
    /// generarles un id local nuevo (ver <see cref="SyncIndex.LoadIdMap"/>). <paramref name="mintId"/>
    /// acuña el id de un item SIN override (default: UUIDv4 aleatorio, ver ADR-06) -- inyectable para
    /// tests que necesitan un id predecible. Los pares acuñados en ESTE scan quedan en
    /// <see cref="LocalSnapshot.MintedIds"/>.
    /// </summary>
    public LocalSnapshot ScanDetailed(
        string rootPath,
        IReadOnlyDictionary<string, string>? idOverrides = null,
        Func<string, string>? mintId = null)
    {
        idOverrides ??= new Dictionary<string, string>();
        mintId ??= _ => Guid.NewGuid().ToString();
        var ws = Workspace.OpenOrCreate(rootPath);

        var items = new Dictionary<string, SyncItemState>();
        var projects = new Dictionary<string, LocalProjectEntry>();
        var transcriptions = new Dictionary<string, LocalTranscriptionEntry>();
        var mintedIds = new Dictionary<string, string>();

        foreach (var project in ws.ListProjects())
        {
            string? projectId = null;
            if (!project.IsGeneral)
            {
                var pathKey = ProjectPathKey(project.Name);
                if (idOverrides.TryGetValue(pathKey, out var overriddenId))
                {
                    projectId = overriddenId;
                }
                else
                {
                    projectId = mintId(pathKey);
                    mintedIds[pathKey] = projectId;
                }

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

                string trId;
                if (hasOverride)
                {
                    trId = overriddenTrId!;
                }
                else
                {
                    trId = mintId(trPathKey);
                    mintedIds[trPathKey] = trId;
                }

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

        return new LocalSnapshot { Items = items, Projects = projects, Transcriptions = transcriptions, MintedIds = mintedIds };
    }

    /// <summary>Clave estable de un proyecto, previa al hash (útil para el mapa de identidad).</summary>
    public static string ProjectPathKey(string projectName) => $"project:{projectName}";

    /// <summary>Clave estable de una transcripción, previa al hash.</summary>
    public static string TranscriptionPathKey(string? projectName, string audioFileName) =>
        $"transcription:{projectName}/{audioFileName}";

    /// <summary>
    /// Resuelve el id de sync de una transcripción (proyecto, archivo de audio) SOLO si ya es
    /// conocido -- sin tener que correr un scan completo del disco. Pensado para
    /// <see cref="SyncCoordinator.MarkAudioDeletedForSync"/> (bug #1, borrado local no propagado a
    /// la nube): en el momento del borrado hace falta resolver el id de sync de ESE audio puntual
    /// para registrar su tombstone. ADR-06: ya no hay <c>HashId</c> con el que "adivinar" un id para
    /// un ítem sin entrada en <paramref name="idMap"/> -- inventar uno acá sintetizaría una
    /// identidad nueva por inferencia (la misma doctrina que ya protege
    /// <see cref="SyncEngine"/>.MergeWithLocalTombstones) y ese id nunca calzaría con el que
    /// <see cref="ScanDetailed"/> vaya a acuñar. <c>null</c> = "no se sabe qué borrar": el caller NO
    /// debe registrar tombstone.
    /// </summary>
    public static string? ResolveTranscriptionId(string? projectName, string audioFileName, IReadOnlyDictionary<string, string> idMap)
    {
        var key = TranscriptionPathKey(projectName, audioFileName);
        return idMap.TryGetValue(key, out var mapped) ? mapped : null;
    }
}
