using System.Net.Http.Headers;
using System.Text.Json;
using AudioTranscriber.Core.Audio;
using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.Sync;

/// <summary>Resultado de un ciclo de sync.</summary>
public enum SyncOutcome
{
    /// <summary>Se ejecutaron las acciones y se guardó la nueva baseline.</summary>
    Completed,

    /// <summary>Se abortó SIN ejecutar nada: el freno anti-borrado-masivo pide confirmación explícita.</summary>
    ConfirmationPending,
}

/// <summary>Resultado de <see cref="SyncEngine.RunAsync"/>.</summary>
public sealed record SyncResult(
    SyncOutcome Outcome,
    IReadOnlyList<SyncAction> Actions,
    int DeleteCount,
    int BaselineCount,
    string? Message = null,
    /// <summary>Cantidad de proyectos que trajo el pull de este ciclo (antes de reconciliar).</summary>
    int PulledProjectsCount = 0,
    /// <summary>Cantidad de transcripciones que trajo el pull de este ciclo (antes de reconciliar).</summary>
    int PulledTranscriptionsCount = 0,
    /// <summary>
    /// Transcripciones bajadas este ciclo cuyo audio NO se pudo descargar (quedan con .txt pero
    /// sin audio, y NO se marcan sincronizadas -- ver <see cref="SyncEngine.RunAsync"/> -- así que
    /// se reintentan solas en el próximo ciclo). Diagnóstico para poder loguear "qué se descarta y
    /// por qué" sin tener que instrumentar excepciones (acá no hay ninguna: es best-effort).
    /// </summary>
    int AudioDownloadFailures = 0,
    /// <summary>Ítems (proyectos + transcripciones) pusheados al backend este ciclo.</summary>
    int PushedCount = 0,
    /// <summary>
    /// Línea por línea, qué hizo el ciclo con cada transcripción pulleada y por qué (aplicada,
    /// solo-texto, audio pendiente de reintento, proyecto no resuelto, omitida sin cambios, etc.).
    /// Pensado para loguearse siempre (no solo en error) vía <c>SyncCycleLogFormatter</c>/
    /// <c>SyncCoordinator</c> en la App -- antes no había forma de saber, sin reproducir el bug a
    /// mano, si un ítem se aplicó, se descartó o por qué. Ver changelog 2026-07-08.
    /// </summary>
    IReadOnlyList<string>? Diagnostics = null,
    /// <summary>
    /// Audios que NO se subieron a la nube este ciclo porque son demasiado grandes (413) o de
    /// formato no soportado (415) -- ver <see cref="SyncFailureClassifier"/>. NO se reintentan (la
    /// baseline avanza) hasta que el archivo cambie. Se cuenta para poder avisarle al usuario que
    /// esos audios quedaron solo en su equipo, en vez de fallar en silencio para siempre.
    /// </summary>
    int OversizeUploadSkips = 0);

/// <summary>
/// Orquesta un ciclo completo de sync: pull remoto, scan local, reconciliación (3-way) y
/// ejecución de las acciones resultantes, con el freno anti-borrado-masivo del diseño
/// (07-diseno-cliente-sync.md, sección "Salvaguardas") como paso obligatorio antes de tocar nada.
/// Todas las dependencias son inyectadas para poder testear con fakes (HttpMessageHandler falso,
/// SyncIndex sobre un archivo temporal, carpeta temporal para el scan).
/// </summary>
public sealed class SyncEngine
{
    /// <summary>
    /// Umbral (sobre el tamaño de la baseline) de borrados combinados (push+pull) que dispara
    /// el freno. No está fijado en el diseño con un número exacto ("si se detecta un porcentaje
    /// alto de borrados de golpe... pausa y pide confirmación"): se adoptó 40% por instrucción
    /// explícita de esta tarea.
    /// </summary>
    public const double MassDeletionThreshold = 0.4;

    private readonly SyncApiClient _api;
    private readonly HttpClient _http;
    private readonly SyncIndex _index;
    private readonly LocalScanner _scanner;
    private readonly RemoteMapper _remoteMapper;
    private readonly SyncPlanner _planner;
    private readonly string _rootPath;
    private readonly string _backendBaseUrl;
    private readonly AudioCompressor _compressor = new();

    public SyncEngine(
        SyncApiClient apiClient,
        HttpClient httpClient,
        SyncIndex index,
        LocalScanner scanner,
        RemoteMapper remoteMapper,
        SyncPlanner planner,
        string rootPath,
        string backendBaseUrl)
    {
        _api = apiClient;
        _http = httpClient;
        _index = index;
        _scanner = scanner;
        _remoteMapper = remoteMapper;
        _planner = planner;
        _rootPath = rootPath;
        _backendBaseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Corre un ciclo de sync. <paramref name="forceConfirmDeletes"/> es el "sí, quiero borrar
    /// igual" explícito del usuario tras un <see cref="SyncOutcome.ConfirmationPending"/> previo
    /// (el diseño no especifica el nombre exacto de este parámetro; se eligió por claridad).
    /// <paramref name="autoUploadUntranscribed"/> (bugfix 2026-07-21: respetar el motor elegido) --
    /// default <c>true</c> para no romper callers/tests existentes -- controla si un audio SIN
    /// transcripción local se sube solo para que la nube (Groq) lo transcriba server-side (ver
    /// <see cref="ExecutePushTranscriptionUpsertAsync"/>). Debe ser <c>false</c> cuando el usuario
    /// eligió el motor Local: si no, el auto-sync le gana de mano transcribiendo con Groq (sin
    /// diarización) antes de que el usuario transcriba local -- bug real reportado.
    /// </summary>
    public async Task<SyncResult> RunAsync(
        string accessToken,
        DateTimeOffset? since = null,
        bool forceConfirmDeletes = false,
        bool autoUploadUntranscribed = true,
        CancellationToken ct = default)
    {
        // Bugfix (freezes de UI): SyncCoordinator dispara este método desde un DispatcherTimer sin
        // Task.Run, así que sin este wrapper todo lo que sigue -- SQLite (LoadBaseline/LoadIdMap) y
        // el scan de disco de todos los .txt del workspace (ScanDetailed) -- corría directo en el
        // hilo de UI. Se envuelve cada porción síncrona en su propio Task.Run, sin tocar el orden
        // ni el resultado: el resto del método sigue usando estas variables exactamente igual que
        // antes, solo cambia EN QUÉ HILO se hace el trabajo pesado de disco.
        var baseline = await Task.Run(() => _index.LoadBaseline(), ct);
        var idOverrides = await Task.Run(() => _index.LoadIdMap(), ct);
        var localTombstones = await Task.Run(() => _index.LoadLocalTombstones(), ct);

        var pull = await _api.PullAsync(accessToken, since, ct);
        var remote = _remoteMapper.Map(pull);

        var localSnapshot = await Task.Run(() => _scanner.ScanDetailed(_rootPath, idOverrides), ct);
        var local = MergeWithLocalTombstones(localSnapshot.Items, baseline, localTombstones);

        var actions = _planner.Plan(baseline, local, remote)
            // Proyectos primero: una transcripción bajada necesita que su carpeta de proyecto
            // ya exista en este mismo ciclo.
            .OrderBy(a => a.Kind == SyncItemKind.Project ? 0 : 1)
            .ToList();

        var deleteCount = actions.Count(a => a.Type is SyncActionType.PushDelete or SyncActionType.PullDelete);

        // Diagnóstico del ciclo (siempre, no solo en error -- ver SyncCoordinator/SyncCycleLogFormatter
        // en la App, que loguean esto a logs/sync-yyyyMMdd.log en cada corrida). Arranca con los
        // proyectos/transcripciones que el pull trajo pero que NO generaron ninguna acción este
        // ciclo (mismo hash que la baseline -> "sin cambios", el caso normal en régimen).
        var diagnostics = new List<string>();
        var actionIds = new HashSet<string>(actions.Select(a => a.Id));
        var omittedProjects = pull.Projects.Count(p => !actionIds.Contains(p.Id));
        var omittedTranscriptions = pull.Transcriptions.Count(t => !actionIds.Contains(t.Id));
        if (omittedProjects > 0 || omittedTranscriptions > 0)
        {
            diagnostics.Add(
                $"{omittedProjects} proyecto(s) y {omittedTranscriptions} transcripción(es) del pull " +
                "sin cambios respecto a la última sync (omitidas, ya existían).");
        }

        // Freno anti-borrado-masivo: nunca se evalúa contra una baseline vacía (primer arranque),
        // porque ahí no puede haber "borrados" reales todavía (ver regla 3 del diseño).
        if (!forceConfirmDeletes && baseline.Count > 0 && deleteCount > baseline.Count * MassDeletionThreshold)
        {
            return new SyncResult(
                SyncOutcome.ConfirmationPending,
                actions,
                deleteCount,
                baseline.Count,
                $"Se detectaron {deleteCount} borrados de {baseline.Count} items en la baseline " +
                $"(> {MassDeletionThreshold:P0}). Confirmá explícitamente para continuar.",
                pull.Projects.Count,
                pull.Transcriptions.Count,
                Diagnostics: diagnostics);
        }

        var ws = Workspace.OpenOrCreate(_rootPath);
        var newBaseline = new Dictionary<string, SyncBaselineItem>(baseline);
        var newIdOverrides = new Dictionary<string, string>(idOverrides);

        // Resuelve el nombre de carpeta de un proyecto por su id remoto, para poder ubicar
        // transcripciones bajadas dentro de su proyecto (se va completando a medida que se
        // procesan altas de proyecto en este mismo ciclo).
        var projectFolderNameById = localSnapshot.Projects.Values.ToDictionary(p => p.Id, p => p.Name);

        var pushProjects = new PushBucket<ProjectUpsert>();
        var pushTranscriptions = new PushBucket<TranscriptionUpsert>();
        var audioDownloadFailures = 0;

        // Bugfix 2026-07-10 (MEDIUM: un ítem no-sincronizable trababa TODO el sync): ids cuya
        // acción tiró una excepción de "fallo esperable" este ciclo (ver catch más abajo) -- se
        // excluyen de la reconstrucción de baseline de más abajo para que se reintenten solos el
        // próximo ciclo, sin bloquear al resto del batch.
        var failedIds = new HashSet<string>();
        var oversizeUploadSkips = 0;

        foreach (var action in actions)
        {
            try
            {
                switch (action.Type)
                {
                    case SyncActionType.PushUpsert when action.Kind == SyncItemKind.Project:
                        ExecutePushProjectUpsert(action, localSnapshot, pushProjects);
                        break;

                    case SyncActionType.PushUpsert when action.Kind == SyncItemKind.Transcription:
                        await ExecutePushTranscriptionUpsertAsync(action, localSnapshot, pushTranscriptions, accessToken, autoUploadUntranscribed, ct);
                        break;

                    case SyncActionType.PushDelete when action.Kind == SyncItemKind.Project:
                        ExecutePushProjectDelete(action, ws, localSnapshot, pushProjects);
                        break;

                    case SyncActionType.PushDelete when action.Kind == SyncItemKind.Transcription:
                        ExecutePushTranscriptionDelete(action, localSnapshot, pushTranscriptions);
                        break;

                    case SyncActionType.PullUpsert when action.Kind == SyncItemKind.Project:
                        ExecutePullProjectUpsert(action, ws, pull, projectFolderNameById, newIdOverrides);
                        break;

                    case SyncActionType.PullUpsert when action.Kind == SyncItemKind.Transcription:
                        // Decisión explícita (pedido 2026-07-08, ver ExecutePullTranscriptionUpsertAsync):
                        // se marca sincronizada SIEMPRE, tenga o no audio. Antes (v1.0.11), si
                        // audio_url_signed venía pero la descarga fallaba, el id quedaba AFUERA de
                        // newBaseline para reintentar solo en el próximo ciclo -- pero mientras tanto el
                        // .txt ya escrito quedaba en un LIMBO invisible (LocalScanner enumeraba por
                        // archivo de audio, no por .txt), y encima -- ver Workspace.ListAudiosIn, que
                        // ahora SÍ enumera .txt huérfanos -- ese limbo generaba un id local NUEVO/distinto
                        // en cada scan (no había override registrado todavía) con riesgo real de
                        // duplicar la transcripción en el próximo push. Dar de baja el reintento
                        // indefinido y resolver a "solo texto" en el momento evita ambos problemas: sin
                        // audio o con descarga fallida, la transcripción queda visible YA (con id
                        // estable) en vez de perdida en un limbo silencioso.
                        var audioDownloadFailed = await ExecutePullTranscriptionUpsertAsync(action, ws, pull, projectFolderNameById, newIdOverrides, diagnostics, ct);
                        if (audioDownloadFailed)
                            audioDownloadFailures++;
                        break;

                    case SyncActionType.PullDelete:
                        ExecutePullDelete(action, ws, localSnapshot);
                        break;
                }
            }
            catch (Exception ex) when (SyncFailureClassifier.IsPermanentUploadFailure(ex))
            {
                // Falla PERMANENTE para estos bytes (413 audio muy grande / 415 formato): reintentar
                // cada ciclo es martillar al pedo -- el archivo siempre va a pesar/ser lo mismo, así
                // que nunca va a andar. A diferencia del catch de abajo, NO se agrega a failedIds:
                // eso deja que la baseline AVANCE para este ítem (ver successfulActions), o sea, el
                // sync lo da por "sin cambios pendientes" y deja de intentarlo. Si el archivo cambia
                // después (se transcribe local, se comprime, se regraba), su hash cambia y el planner
                // lo vuelve a proponer solo. Se cuenta para avisarle al usuario que ese audio NO
                // quedó en la nube (ver OversizeUploadSkips en SyncResult).
                oversizeUploadSkips++;
                var name = localSnapshot.Transcriptions.TryGetValue(action.Id, out var e)
                    ? e.AudioFileName : action.Id;
                diagnostics.Add(
                    $"{action.Kind} '{name}': muy grande para la nube ({ex.Message}) -- se omitió (no se reintenta hasta que el archivo cambie).");
            }
            catch (Exception ex) when (ex is SyncApiException or HttpRequestException or IOException)
            {
                // Bugfix 2026-07-10 (MEDIUM: un audio no-subible trababa TODO el sync): antes, una
                // excepción acá (500 persistente del backend, disco lleno, red, etc.) abortaba el
                // foreach ENTERO -- el push batch acumulado hasta ese punto nunca se mandaba y
                // SaveBaseline/SaveIdMap no corrían, así que la baseline nunca avanzaba: el mismo ítem
                // fallido se reintentaba y volvía a abortar CADA ciclo, indefinidamente (sync trabado
                // para siempre, ni siquiera cambios sin relación convergían). Atrapar por acción
                // contiene el fallo a este único ítem: el resto del batch sigue, se pushea, y la
                // baseline avanza para todo lo que sí funcionó (ver failedIds más abajo). A diferencia
                // del catch de arriba, esto SÍ se reintenta el próximo ciclo: es una falla que puede
                // resolverse sola (se recupera la red, baja la carga del server, se libera disco).
                failedIds.Add(action.Id);
                diagnostics.Add(
                    $"{action.Kind} '{action.Id}': falló ({ex.GetType().Name}: {ex.Message}) -- se reintenta el próximo ciclo.");
            }
        }

        // ---- Baseline: re-ancla los dos hashes de cada ítem tocado con éxito este ciclo ---------
        // Bugfix 2026-07-10 (oscilación perpetua, ver SyncPlanner/SyncBaselineItem): un PullUpsert
        // reescribe el .txt/audio, y el hash LOCAL de una transcripción incluye el mtime del archivo
        // (ver LocalScanner.ScanDetailed) -- así que el hash local post-escritura difiere del que
        // tenía ANTES de aplicar las acciones de este ciclo. Un rescan local ÚNICO, hecho DESPUÉS de
        // que TODAS las escrituras ya se aplicaron, captura el hash local tal como quedó de verdad.
        var successfulActions = actions.Where(a => !failedIds.Contains(a.Id)).ToList();
        if (successfulActions.Count > 0)
        {
            var rescan = (await Task.Run(() => _scanner.ScanDetailed(_rootPath, newIdOverrides), ct)).Items;
            foreach (var action in successfulActions)
                newBaseline[action.Id] = BuildBaselineEntry(action, local, remote, baseline, rescan);
        }

        string? cascadeDeleteWarning = null;
        if (HasContent(pushProjects) || HasContent(pushTranscriptions))
        {
            var request = new PushRequest
            {
                Projects = HasContent(pushProjects) ? pushProjects : null,
                Transcriptions = HasContent(pushTranscriptions) ? pushTranscriptions : null,
            };
            var pushResponse = await _api.PushAsync(accessToken, request, ct);
            cascadeDeleteWarning = ResolveCascadeDeleteRejections(
                pushResponse, pushProjects, baseline, newBaseline, localSnapshot);
        }

        await Task.Run(() => _index.SaveBaseline(newBaseline), ct);
        await Task.Run(() => _index.SaveIdMap(newIdOverrides), ct);

        // Limpieza de tombstones ya resueltos (bug #1): un tombstone se da por resuelto si su id
        // terminó Deleted=true en la nueva baseline (el PushDelete se ejecutó y se pusheó con
        // éxito) o si directamente nunca estuvo en la baseline (stale -- nada que borrarle al
        // servidor, ver RunAsync_TombstoneParaIdQueNoEstaEnBaseline... en SyncEngineTests). Los que
        // quedaron pendientes por un fallo de red (ver failedIds arriba: newBaseline conserva el
        // valor previo, Deleted=false) NO se limpian acá -- se reintentan solos el próximo ciclo.
        if (localTombstones.Count > 0)
        {
            var resolvedTombstoneIds = localTombstones.Keys
                .Where(id => !newBaseline.TryGetValue(id, out var item) || item.Deleted)
                .ToList();
            if (resolvedTombstoneIds.Count > 0)
                await Task.Run(() => _index.RemoveLocalTombstones(resolvedTombstoneIds), ct);
        }

        var pushedCount = pushProjects.Upserts.Count + pushProjects.Deletes.Count
            + pushTranscriptions.Upserts.Count + pushTranscriptions.Deletes.Count;

        return new SyncResult(
            SyncOutcome.Completed,
            actions,
            deleteCount,
            baseline.Count,
            cascadeDeleteWarning,
            pull.Projects.Count,
            pull.Transcriptions.Count,
            audioDownloadFailures,
            pushedCount,
            diagnostics,
            oversizeUploadSkips);
    }

    /// <summary>
    /// Reacciona a los rechazos de borrado-en-cascada (bug C1, ver <see cref="PushErrorHandling"/>)
    /// que hayan venido en <c>errors[]</c> de la respuesta del push. El resto de errores del batch
    /// (proyecto inválido, error SQL, etc.) NO se toca acá: siguen el comportamiento de reintento
    /// normal que ya tiene el resto del sync (el ítem sigue "dirty" porque nada revierte su entrada
    /// en <paramref name="newBaseline"/>, así que el próximo ciclo lo vuelve a intentar).
    /// <para/>
    /// Para un proyecto rechazado: el desktop ya lo movió a `.papelera` local de forma optimista
    /// (<see cref="ExecutePushProjectDelete"/>, ANTES de conocer el resultado del push), pero el
    /// servidor NO lo borró. Dejar <paramref name="newBaseline"/> marcando "borrado" sería mentira
    /// (el proyecto sigue vivo en la nube con sus datos) y además dispararía una resurrección rara
    /// vía PullUpsert en el próximo ciclo (el remoto seguiría "cambiado" contra esa baseline
    /// incorrecta). Se revierte la entrada al valor que tenía ANTES de este ciclo (o se la saca si
    /// no existía), dejando el ítem resuelto como "necesita acción manual" en vez de dirty/pendiente
    /// para siempre -- ya no se vuelve a intentar el mismo borrado cada ciclo.
    /// </summary>
    private static string? ResolveCascadeDeleteRejections(
        PushResponse pushResponse,
        PushBucket<ProjectUpsert> pushProjects,
        IReadOnlyDictionary<string, SyncBaselineItem> originalBaseline,
        Dictionary<string, SyncBaselineItem> newBaseline,
        LocalSnapshot localSnapshot)
    {
        List<string>? warnings = null;

        foreach (var error in pushResponse.Errors)
        {
            var rejection = PushErrorHandling.TryParseCascadeDeleteRejection(error);
            if (rejection is null || !PushErrorHandling.ShouldSkipRetry(rejection))
                continue;
            if (!pushProjects.Deletes.Contains(rejection.ProjectId))
                continue; // defensivo: no tocar nada que este ciclo no haya intentado borrar

            if (originalBaseline.TryGetValue(rejection.ProjectId, out var original))
                newBaseline[rejection.ProjectId] = original;
            else
                newBaseline.Remove(rejection.ProjectId);

            var displayName = localSnapshot.Projects.TryGetValue(rejection.ProjectId, out var project)
                ? project.Name
                : rejection.ProjectId;

            (warnings ??= new List<string>()).Add(
                $"El proyecto '{displayName}' tiene subcarpetas en la nube; borralo desde la web.");
        }

        return warnings is null ? null : string.Join(" ", warnings);
    }

    /// <summary>
    /// Construye la entrada de baseline para un ítem tocado con éxito este ciclo, re-anclando AMBOS
    /// hashes por separado (bugfix 2026-07-10, oscilación perpetua -- ver <see cref="SyncPlanner"/>/
    /// <see cref="SyncBaselineItem"/>). <paramref name="rescan"/> es un scan local ÚNICO hecho
    /// DESPUÉS de que todas las escrituras de este ciclo (pull) ya se aplicaron: un PullUpsert
    /// reescribe el .txt/audio, y el hash local de una transcripción incluye el mtime del archivo
    /// (ver <see cref="LocalScanner.ScanDetailed"/>), así que el hash local post-escritura difiere
    /// del que tenía <paramref name="local"/> (calculado ANTES de aplicar las acciones de este
    /// ciclo) -- <paramref name="rescan"/> es la única fuente confiable para
    /// <see cref="SyncBaselineItem.LastLocalHash"/> tras un pull. Para un push, el archivo local no
    /// cambia, así que <paramref name="rescan"/> coincide con <paramref name="local"/> de todos
    /// modos (se usa igual por uniformidad).
    /// <para/>
    /// <see cref="SyncBaselineItem.LastRemoteHash"/> se toma del pull YA HECHO este ciclo (no hace
    /// falta re-pullear); si el id no vino en el pull (p.ej. un ítem local nuevo recién pusheado,
    /// cuyo eco todavía no llegó) se preserva el valor previo de la baseline -- el próximo ciclo,
    /// un pull genuino confirma el valor real. Para un borrado, no queda contenido que rescanear
    /// (el archivo ya se movió a <c>.papelera</c>): se preserva el hash previo de la baseline, dato
    /// que ya no importa demasiado porque <see cref="SyncBaselineItem.Deleted"/> es lo que de verdad
    /// gobierna la comparación de ausencia (ver <see cref="SyncPlanner"/>).
    /// </summary>
    private static SyncBaselineItem BuildBaselineEntry(
        SyncAction action,
        IReadOnlyDictionary<string, SyncItemState> local,
        IReadOnlyDictionary<string, SyncItemState> remote,
        IReadOnlyDictionary<string, SyncBaselineItem> previousBaseline,
        IReadOnlyDictionary<string, SyncItemState> rescan)
    {
        var isDelete = action.Type is SyncActionType.PushDelete or SyncActionType.PullDelete;
        var isPush = action.Type is SyncActionType.PushUpsert or SyncActionType.PushDelete;

        // "Ganador" de esta acción (de dónde sale UpdatedAt, ver SyncPlanner.ToAction: push -> local
        // gana, pull -> remoto gana).
        DateTimeOffset updatedAt;
        if (isPush && local.TryGetValue(action.Id, out var pushWinner))
            updatedAt = pushWinner.UpdatedAt;
        else if (!isPush && remote.TryGetValue(action.Id, out var pullWinner))
            updatedAt = pullWinner.UpdatedAt;
        else
            updatedAt = DateTimeOffset.UtcNow; // defensivo: no debería pasar (ver SyncPlanner.ToAction)

        string lastLocalHash;
        if (isDelete)
            lastLocalHash = previousBaseline.TryGetValue(action.Id, out var pbDel) ? pbDel.LastLocalHash : string.Empty;
        else if (rescan.TryGetValue(action.Id, out var rescanned))
            lastLocalHash = rescanned.ContentHash;
        else if (local.TryGetValue(action.Id, out var l))
            lastLocalHash = l.ContentHash;
        else
            lastLocalHash = previousBaseline.TryGetValue(action.Id, out var pb) ? pb.LastLocalHash : string.Empty;

        var lastRemoteHash = remote.TryGetValue(action.Id, out var r)
            ? r.ContentHash
            : previousBaseline.TryGetValue(action.Id, out var pb2) ? pb2.LastRemoteHash : string.Empty;

        return new SyncBaselineItem(action.Id, action.Kind, lastLocalHash, lastRemoteHash, updatedAt, isDelete);
    }

    // ---- Push ---------------------------------------------------------------

    private static void ExecutePushProjectUpsert(SyncAction action, LocalSnapshot local, PushBucket<ProjectUpsert> bucket)
    {
        var entry = local.Projects[action.Id];
        bucket.Upserts.Add(new ProjectUpsert
        {
            Id = entry.Id,
            Name = entry.Title,
            Description = entry.Description,
        });
    }

    /// <summary>
    /// Bugfix 2026-07-21 (no respetar el motor elegido): para un audio SIN transcripción local, el
    /// upload automático a la nube (para que Groq lo transcriba server-side) ahora es condicional a
    /// <paramref name="autoUploadUntranscribed"/> -- antes se subía SIEMPRE, ignorando que el
    /// usuario tuviera elegido el motor Local (+ diarización), así que el auto-sync (cada 60s) le
    /// ganaba de mano transcribiendo con Groq (sin hablantes) antes de que el usuario transcribiera
    /// local. En <c>false</c>, simplemente se hace <c>return</c> SIN excepción: la acción cuenta
    /// como "exitosa" este ciclo (no entra a <c>failedIds</c>, ver <see cref="RunAsync"/>), así que
    /// <see cref="BuildBaselineEntry"/> re-ancla la baseline al estado local actual (audio sin
    /// texto) -- NO se re-propone en loop cada ciclo. Cuando el usuario transcriba local,
    /// <see cref="LocalTranscriptionEntry.HasLocalTranscript"/> pasa a <c>true</c>, el hash local
    /// cambia (texto + mtime, ver <see cref="LocalScanner"/>) y el próximo ciclo SÍ sube la
    /// transcripción (con texto, por el camino normal de <paramref name="bucket"/>).
    /// </summary>
    private async Task ExecutePushTranscriptionUpsertAsync(
        SyncAction action, LocalSnapshot local, PushBucket<TranscriptionUpsert> bucket, string accessToken,
        bool autoUploadUntranscribed, CancellationToken ct)
    {
        var entry = local.Transcriptions[action.Id];

        if (!entry.HasLocalTranscript)
        {
            if (!autoUploadUntranscribed)
                return; // motor Local: NO auto-transcribir en la nube; se espera a que el usuario transcriba local.

            // Audio nuevo sin transcripción local: la fuente de verdad de la transcripción es
            // siempre la nube, así que se sube el audio para que el backend lo transcriba
            // (Groq server-side), en vez de mandar metadata de texto.
            await UploadAudioAsync(entry, accessToken, ct);
            return;
        }

        bucket.Upserts.Add(new TranscriptionUpsert
        {
            Id = entry.Id,
            Text = entry.Text,
            ProjectId = entry.ProjectId,
        });
    }

    private async Task UploadAudioAsync(LocalTranscriptionEntry entry, string accessToken, CancellationToken ct)
    {
        // Fix 2+3: comprimir a opus y subir DIRECTO a Storage con un signed upload URL
        // (/api/audio/prepare), salteando el tope duro de ~4,5 MB del body de la función de Vercel;
        // después /api/transcribe transcribe desde Storage. El audio del desktop es WAV sin comprimir
        // (~1,8 MB/min); a opus queda ~10x más chico. Si no se puede comprimir (formato raro/corrupto)
        // se cae al camino viejo de subir el archivo crudo por el body -- si es grande dará 413, que
        // el clasificador de fallas permanentes maneja sin loop (ver SyncFailureClassifier).
        string? tempOpus = Path.Combine(Path.GetTempPath(), $"atup_{Guid.NewGuid():N}.ogg");
        try
        {
            try
            {
                _compressor.CompressToOpus(entry.AudioPath, tempOpus, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (File.Exists(tempOpus))
                    File.Delete(tempOpus);
                tempOpus = null; // sin comprimir: respaldo por el camino crudo
            }

            if (tempOpus is not null)
            {
                var prepared = await PrepareDirectUploadAsync(entry.AudioFileName, accessToken, ct);
                await PutAudioToSignedUrlAsync(prepared.SignedUrl, prepared.ApiKey, tempOpus, ct);
                await RequestTranscribeFromStorageAsync(prepared.Path, entry, accessToken, ct);
            }
            else
            {
                await UploadRawAudioViaBodyAsync(entry, accessToken, ct);
            }
        }
        finally
        {
            if (tempOpus is not null && File.Exists(tempOpus))
                File.Delete(tempOpus);
        }
    }

    /// <summary>Pide a <c>/api/audio/prepare</c> un signed upload URL para subir el audio comprimido directo a Storage.</summary>
    private async Task<PreparedUpload> PrepareDirectUploadAsync(string audioName, string accessToken, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { audioName, ext = ".ogg" });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_backendBaseUrl}/api/audio/prepare")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException($"Preparar la subida falló ({(int)resp.StatusCode}): {body}", (int)resp.StatusCode);

        return PreparedUpload.Parse(body);
    }

    /// <summary>
    /// PUT directo del archivo comprimido al signed upload URL de Supabase Storage (no pasa por
    /// Vercel). El token de subida va en la URL (<c>?token=</c>), pero el gateway de Supabase igual
    /// exige el <c>apikey</c> en toda ruta <c>/storage/v1/</c>: se mandan los MISMOS headers que el
    /// storage-js oficial en <c>uploadToSignedUrl</c> (apikey + Authorization con la anon key,
    /// x-upsert, content-type, cache-control), para no depender de comportamientos no documentados.
    /// </summary>
    private async Task PutAudioToSignedUrlAsync(string signedUrl, string apiKey, string filePath, CancellationToken ct)
    {
        await using var fileStream = File.OpenRead(filePath);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        using var req = new HttpRequestMessage(HttpMethod.Put, signedUrl) { Content = fileContent };
        req.Headers.TryAddWithoutValidation("apikey", apiKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.TryAddWithoutValidation("x-upsert", "false");
        req.Headers.TryAddWithoutValidation("cache-control", "max-age=3600");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new SyncApiException($"Subida de audio falló ({(int)resp.StatusCode}): {body}", (int)resp.StatusCode);
        }
    }

    /// <summary>Le pide a <c>/api/transcribe</c> que transcriba el audio YA subido a Storage (modo storagePath, sin body).</summary>
    private async Task RequestTranscribeFromStorageAsync(string storagePath, LocalTranscriptionEntry entry, string accessToken, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(storagePath), "storagePath" },
            { new StringContent(entry.AudioFileName), "audioName" }, // nombre de display; la identidad se mantiene
            { new StringContent(entry.Id), "id" },
            // Fuerza el modelo de máxima calidad en las subidas automáticas (el backend defaultea a "turbo").
            { new StringContent("whisper-large-v3"), "model" },
        };
        if (entry.ProjectId is not null)
            content.Add(new StringContent(entry.ProjectId), "projectId");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_backendBaseUrl}/api/transcribe") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new SyncApiException($"Transcribir desde Storage falló ({(int)resp.StatusCode}): {body}", (int)resp.StatusCode);
        }
    }

    /// <summary>Respaldo: sube el archivo crudo por el body de <c>/api/transcribe</c> (el de siempre, con su tope de ~4,5 MB).</summary>
    private async Task UploadRawAudioViaBodyAsync(LocalTranscriptionEntry entry, string accessToken, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(entry.AudioPath);
        using var fileContent = new StreamContent(fileStream);
        content.Add(fileContent, "file", entry.AudioFileName);
        content.Add(new StringContent(entry.Id), "id");
        if (entry.ProjectId is not null)
            content.Add(new StringContent(entry.ProjectId), "projectId");
        content.Add(new StringContent("whisper-large-v3"), "model");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_backendBaseUrl}/api/transcribe") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new SyncApiException($"Subida de audio falló ({(int)resp.StatusCode}): {body}", (int)resp.StatusCode);
        }
    }


    private void ExecutePushProjectDelete(SyncAction action, Workspace ws, LocalSnapshot local, PushBucket<ProjectUpsert> bucket)
    {
        // El item local puede ya no existir en disco (el borrado se detectó por AUSENCIA contra
        // la baseline, ver MergeWithLocalTombstones): en ese caso no hay nada que mover, solo
        // hace falta avisarle al servidor.
        if (local.Projects.TryGetValue(action.Id, out var entry))
            MoveProjectToPapelera(ws, entry.Name, entry.FolderPath);
        bucket.Deletes.Add(action.Id);
    }

    private void ExecutePushTranscriptionDelete(SyncAction action, LocalSnapshot local, PushBucket<TranscriptionUpsert> bucket)
    {
        if (local.Transcriptions.TryGetValue(action.Id, out var entry))
            MoveTranscriptionToPapelera(entry.ProjectName, entry.AudioFileName, entry.AudioPath, entry.HasLocalTranscript ? entry.TranscriptPath : null);
        bucket.Deletes.Add(action.Id);
    }

    // ---- Pull -----------------------------------------------------------------

    private void ExecutePullProjectUpsert(
        SyncAction action, Workspace ws, PullResponse pull,
        Dictionary<string, string> projectFolderNameById, Dictionary<string, string> idOverrides)
    {
        var remoteProject = pull.Projects.First(p => p.Id == action.Id);

        // Bugfix 2026-07-10 (MEDIUM: rename remoto borra el color y huérfana la carpeta vieja):
        // esto SIEMPRE resolvía por NOMBRE con CreateProject(remoteProject.Name) -- para un
        // proyecto renombrado del lado web (mismo id, nombre nuevo) eso crea una carpeta NUEVA
        // vacía (Color=null, sin audios) en vez de encontrar la carpeta VIEJA que ya tiene el
        // color y los audios del usuario. La carpeta vieja quedaba huérfana en disco, y ambas
        // terminaban resolviendo al mismo id (colisión en LocalScanner). Si projectFolderNameById
        // ya conoce este id con OTRO nombre de carpeta (viene del scan local al INICIO de este
        // ciclo, ver RunAsync), es un rename: se MUEVE la carpeta existente (RenameProject, que
        // preserva _proyecto.json con su color y los audios) en vez de crear una nueva, y se
        // descarta la entrada vieja de idOverrides.
        if (projectFolderNameById.TryGetValue(action.Id, out var oldFolderName)
            && !string.Equals(oldFolderName, Workspace.Sanitize(remoteProject.Name), StringComparison.OrdinalIgnoreCase))
        {
            var existing = ws.ListProjects().FirstOrDefault(p => !p.IsGeneral && p.Name == oldFolderName);
            if (existing is not null)
            {
                ws.RenameProject(existing, remoteProject.Name);
                idOverrides.Remove(LocalScanner.ProjectPathKey(oldFolderName));
            }
        }

        var project = ws.CreateProject(remoteProject.Name);
        project.Title = remoteProject.Name;
        project.Description = remoteProject.Description;
        // Color is a local-only field ("color por proyecto/carpeta", v1.0.23): it is never part
        // of the sync DTO, so remoteProject knows nothing about it and CreateProject always
        // returns a fresh AudioProject with Color == null. Without this, every pull-upsert of an
        // already-local project (auto-sync runs every 60s, or a manual "Sincronizar ahora")
        // would silently wipe whatever color the user had picked -- confirmed live bug, see
        // changelog 2026-07-09. Preserve whatever color is already persisted on disk for this
        // project's folder before SaveProjectMeta re-writes _proyecto.json. Revisit this if color
        // is ever synced server-side.
        project.Color = ws.ReadProjectColor(project.FolderPath);
        ws.SaveProjectMeta(project);

        projectFolderNameById[action.Id] = project.Name;
        idOverrides[LocalScanner.ProjectPathKey(project.Name)] = action.Id;
    }

    /// <summary>
    /// Escribe el .txt (siempre) y, si corresponde, intenta bajar el audio. Devuelve
    /// <c>true</c> si HUBO audio que bajar y la descarga falló (solo para diagnóstico -- ver
    /// <see cref="SyncResult.AudioDownloadFailures"/>); <c>false</c> en cualquier otro caso (no
    /// había audio, o se bajó con éxito). La transcripción se marca sincronizada SIEMPRE (ver
    /// caller, <see cref="RunAsync"/>): a diferencia de la versión anterior (v1.0.11), que dejaba
    /// el id afuera de la baseline para reintentar la descarga indefinidamente, ahora "sin audio
    /// que bajar" y "la descarga falló" se tratan IGUAL -- se guarda como transcripción SOLO TEXTO
    /// y se da por completa YA. Decisión explícita (pedido 2026-07-08): dejar el reintento
    /// indefinido convivía mal con que el .txt huérfano ahora es visible en la UI (ver
    /// <see cref="Workspaces.Workspace.ListAudiosIn"/>) -- sin esto, el mismo .txt podía generar un
    /// id local NUEVO en cada scan mientras esperaba el reintento, con riesgo real de duplicarse en
    /// el próximo push. Agrega una línea a <paramref name="diagnostics"/> por cada transcripción
    /// procesada (motivo: solo-texto, audio descargado, descarga fallida, proyecto no resuelto)
    /// para poder loguear siempre el detalle del ciclo sin reproducir el bug a mano.
    /// </summary>
    private async Task<bool> ExecutePullTranscriptionUpsertAsync(
        SyncAction action, Workspace ws, PullResponse pull,
        Dictionary<string, string> projectFolderNameById, Dictionary<string, string> idOverrides,
        List<string> diagnostics, CancellationToken ct)
    {
        var remoteTranscription = pull.Transcriptions.First(t => t.Id == action.Id);
        string? projectFolder = remoteTranscription.ProjectId is not null
            && projectFolderNameById.TryGetValue(remoteTranscription.ProjectId, out var name)
                ? name
                : null; // sin proyecto conocido -> se aloja en General (limitación conocida, ver reporte)

        if (remoteTranscription.ProjectId is not null && projectFolder is null)
        {
            diagnostics.Add(
                $"transcripción '{remoteTranscription.AudioName}': project_id={remoteTranscription.ProjectId} " +
                "no se pudo resolver a un proyecto local conocido este ciclo -- se alojó en General.");
        }

        var transcriptPath = ws.TranscriptPathFor(remoteTranscription.AudioName, projectFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
        File.WriteAllText(transcriptPath, remoteTranscription.Text);

        idOverrides[LocalScanner.TranscriptionPathKey(projectFolder, remoteTranscription.AudioName)] = action.Id;

        // Fix de raíz del bug de pérdida de datos (ver MergeWithLocalTombstones): además del .txt,
        // bajamos el audio si el backend mandó una URL firmada temporal, para que el próximo scan
        // local encuentre la transcripción completa (audio + texto) y no la trate como ausente.
        var audioReady = !string.IsNullOrEmpty(remoteTranscription.AudioUrlSigned)
            && await DownloadAudioBestEffortAsync(ws, remoteTranscription, projectFolder, ct);

        if (audioReady)
        {
            // "OK" cubre dos casos (ver DownloadAudioBestEffortAsync): se descargó recién, o ya
            // había un audio local (el original del usuario) que se preservó sin tocar.
            diagnostics.Add(
                $"transcripción '{remoteTranscription.AudioName}' (proyecto: {projectFolder ?? "General"}): audio OK (descargado o ya presente localmente).");
            return false; // completa: no hubo fallo de descarga que reportar.
        }

        // SOLO TEXTO: no había audio_url_signed, o la descarga falló. Identidad ADICIONAL con el
        // pathKey en base al STEM (sin extensión) del .txt: LocalScanner.ScanDetailed enumera
        // estos items (ver Workspace.ListAudiosIn/AudioItem.HasAudio=false) por el nombre del .txt
        // huérfano, que no tiene forma de reconstruir la extensión original de AudioName -- sin
        // este segundo override, el próximo scan generaría un id NUEVO para el mismo ítem.
        var stem = Path.GetFileNameWithoutExtension(remoteTranscription.AudioName);
        idOverrides[LocalScanner.TranscriptionPathKey(projectFolder, stem)] = action.Id;

        var downloadFailed = !string.IsNullOrEmpty(remoteTranscription.AudioUrlSigned);
        var reason = downloadFailed ? "la descarga del audio falló" : "sin audio_url_signed";
        diagnostics.Add(
            $"transcripción '{remoteTranscription.AudioName}' (proyecto: {projectFolder ?? "General"}): " +
            $"{reason} -- guardada solo con texto y marcada sincronizada (no se reintenta el audio).");
        return downloadFailed;
    }

    /// <summary>
    /// BEST-EFFORT en el sentido de que un fallo acá (red caída, URL vencida, status no-éxito,
    /// excepción cualquiera) NO tira una excepción ni aborta el pull/ciclo de sync completo -- pero
    /// SÍ se le informa el resultado al caller (a diferencia de la versión anterior, que tragaba el
    /// fallo en silencio) para que <see cref="RunAsync"/> decida si puede marcar la transcripción
    /// como sincronizada o si tiene que reintentar en el próximo ciclo.
    /// <para/>
    /// Bugfix 2026-07-21 (pérdida de calidad de audio): si YA hay un audio local en
    /// <c>audioPath</c>, es el ORIGINAL del usuario (mejor calidad que el comprimido que guarda la
    /// nube para que Groq lo transcriba, ver <see cref="UploadAudioAsync"/>) -- NUNCA se pisa. Solo
    /// se baja el comprimido de la nube cuando NO hay audio local todavía (p.ej. un equipo nuevo
    /// pulleando todo de cero). Sin este freno, el ciclo siguiente a una subida bajaba ese mismo
    /// comprimido y pisaba (<c>File.Move</c> con <c>overwrite: true</c>) el WAV de 20MB original con
    /// la copia de ~2MB -- bug real reportado.
    /// </summary>
    private async Task<bool> DownloadAudioBestEffortAsync(
        Workspace ws, RemoteTranscription remoteTranscription, string? projectFolder, CancellationToken ct)
    {
        var audioPath = ws.AudioPathFor(remoteTranscription.AudioName, projectFolder);

        // Integridad de audio (bugfix 2026-07-21): si ya hay un audio local NO vacío para esta
        // transcripción, es el ORIGINAL del usuario (mejor calidad que el comprimido que guarda la
        // nube) -- NUNCA lo pisamos. Solo se baja el comprimido cuando falta un audio local usable
        // (equipo nuevo pulleando de cero, o un archivo 0-byte que no sirve como original). El
        // ">0" cubre el caso raro de un archivo vacío por corrupción externa: ahí sí conviene bajar.
        if (File.Exists(audioPath) && new FileInfo(audioPath).Length > 0)
            return true; // audio local ya presente y usable: no se descarga nada, no es un fallo.

        // Bugfix 2026-07-10 (MEDIUM: re-descarga no atómica corrompe el audio bueno): antes se
        // escribía DIRECTO a audioPath con File.Create, que TRUNCA cualquier archivo existente de
        // una. Si CopyToAsync fallaba a mitad de camino (WiFi cae, cancelación), el catch de abajo
        // tragaba la excepción y quedaba un archivo PARCIAL pisando al bueno -- audio perdido.
        // Ahora se escribe a un temp hermano en el MISMO directorio y se mueve de una sola vez
        // (File.Move es atómico dentro del mismo volumen) SOLO si la descarga terminó OK; el temp
        // se borra ante cualquier fallo, así el archivo previo (si existía) sobrevive intacto.
        var tempPath = audioPath + ".part";
        try
        {
            using var resp = await _http.GetAsync(remoteTranscription.AudioUrlSigned, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
            await using (var fileStream = File.Create(tempPath))
            {
                await resp.Content.CopyToAsync(fileStream, ct);
            }
            File.Move(tempPath, audioPath, overwrite: true);
            return true;
        }
        catch
        {
            // Swallow: descarga de audio es best-effort, no debe tirar abajo el sync. El próximo
            // ciclo reintenta (ver comentario en RunAsync). Limpia el .part parcial si quedó.
            TryDeletePartialFile(tempPath);
            return false;
        }
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort: si el .part no se puede borrar (locked, permisos), no es fatal -- queda
            // huérfano en disco pero no afecta al archivo bueno ni al próximo intento (File.Create
            // lo trunca de nuevo la próxima vez que se reintente la descarga).
        }
    }

    private void ExecutePullDelete(SyncAction action, Workspace ws, LocalSnapshot local)
    {
        if (action.Kind == SyncItemKind.Project && local.Projects.TryGetValue(action.Id, out var project))
        {
            MoveProjectToPapelera(ws, project.Name, project.FolderPath);
        }
        else if (action.Kind == SyncItemKind.Transcription && local.Transcriptions.TryGetValue(action.Id, out var transcription))
        {
            MoveTranscriptionToPapelera(
                transcription.ProjectName, transcription.AudioFileName, transcription.AudioPath,
                transcription.HasLocalTranscript ? transcription.TranscriptPath : null);
        }
        // Si no existe localmente (p.ej. nunca se había bajado), no hay nada que mover.
    }

    // ---- Papelera ---------------------------------------------------------
    // Convención propia (no está en el diseño): en vez de borrar de una, se mueve todo a
    // ".papelera/<timestamp>_<nombre>/" dentro de la raíz sincronizada, preservando la
    // estructura audios/transcripts para poder restaurar a mano si hace falta.

    private void MoveProjectToPapelera(Workspace ws, string projectName, string folderPath)
    {
        var bucket = PapeleraBucketFor(projectName);

        if (Directory.Exists(folderPath))
            Directory.Move(folderPath, Path.Combine(bucket, "audios"));

        var transcriptsFolder = Path.Combine(ws.TranscriptsPath, projectName);
        if (Directory.Exists(transcriptsFolder))
            Directory.Move(transcriptsFolder, Path.Combine(bucket, "transcripts"));
    }

    private void MoveTranscriptionToPapelera(string? projectName, string audioFileName, string audioPath, string? transcriptPath)
    {
        var bucket = PapeleraBucketFor($"{projectName}_{audioFileName}");

        if (File.Exists(audioPath))
        {
            Directory.CreateDirectory(bucket);
            File.Move(audioPath, Path.Combine(bucket, audioFileName));
        }
        if (transcriptPath is not null && File.Exists(transcriptPath))
        {
            Directory.CreateDirectory(bucket);
            File.Move(transcriptPath, Path.Combine(bucket, Path.GetFileName(transcriptPath)));
        }
    }

    private string PapeleraBucketFor(string label)
    {
        var safeLabel = string.Join("_", label.Split(Path.GetInvalidFileNameChars()));
        var bucket = Path.Combine(_rootPath, ".papelera", $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{safeLabel}");
        Directory.CreateDirectory(bucket);
        return bucket;
    }

    // ---- Helpers ------------------------------------------------------------

    private static Dictionary<string, SyncItemState> MergeWithLocalTombstones(
        IReadOnlyDictionary<string, SyncItemState> rawLocal,
        IReadOnlyDictionary<string, SyncBaselineItem> baseline,
        IReadOnlyDictionary<string, SyncItemKind> localTombstones)
    {
        // SEGURIDAD (bugfix de PÉRDIDA DE DATOS): NO sintetizar tombstones por ausencia local.
        //
        // El scan local enumera transcripciones por su archivo de AUDIO. Una transcripción bajada
        // de la nube escribe solo el .txt (el audio todavía no se descarga — gap de Fase D), así que
        // "desaparece" del scan del ciclo siguiente. Sintetizar un tombstone por esa ausencia sola
        // se interpretaba como "borrado local" y se propagaba como PushDelete → BORRABA la nube
        // (caso real: primera sync con carpeta vacía + nube con datos vaciaba la cuenta).
        //
        // La ausencia local SOLA sigue tratándose SIEMPRE como "sin cambios" -- NUNCA se infiere un
        // borrado. Lo único que puede inyectar Deleted=true acá es un tombstone EXPLÍCITO
        // (<paramref name="localTombstones"/>, ver SyncIndex.AddLocalTombstone), registrado por
        // SyncCoordinator.MarkAudioDeletedForSync en el momento exacto en que el usuario tocó
        // "Borrar" (ver Workspace.DeleteAudio). Los borrados hechos DESDE LA WEB siguen aplicándose
        // vía PullDelete (tombstone real que manda el backend) -- este mecanismo es el equivalente
        // LOCAL de esa misma señal explícita, nunca inferida.
        var result = new Dictionary<string, SyncItemState>(rawLocal);

        foreach (var (id, kind) in localTombstones)
        {
            // Nada que borrar / seguridad: sin un baseline vivo para este id (nunca se sincronizó,
            // o ya estaba borrado) no hay ningún borrado real que propagarle al servidor.
            if (!baseline.TryGetValue(id, out var baselineItem) || baselineItem.Deleted)
                continue;

            // El scan SÍ encuentra el item con audio real de nuevo (p.ej. el usuario deshizo el
            // borrado a mano restaurando el archivo antes del próximo ciclo): no pisar un item vivo
            // con un borrado que ya no corresponde.
            if (rawLocal.ContainsKey(id))
                continue;

            result[id] = new SyncItemState(id, kind, baselineItem.LastLocalHash, baselineItem.UpdatedAt, Deleted: true);
        }

        return result;
    }

    private static bool HasContent<T>(PushBucket<T> bucket) => bucket.Upserts.Count > 0 || bucket.Deletes.Count > 0;
}
