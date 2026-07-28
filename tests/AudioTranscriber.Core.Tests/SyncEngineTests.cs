using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;
using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.Tests;

public class SyncEngineTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    // Handler falso que captura cada request (igual patrón que SyncApiClientTests).
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : null);
            return _responder(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Bugfix 2026-07-10: la baseline ahora ancla DOS hashes por ítem (ver SyncBaselineItem /
    // SyncPlanner). Helper de test: convierte un SyncItemState (como el que devuelve un scan local)
    // en una entrada de baseline "ya sincronizada", con el mismo hash en ambos lados por default --
    // alcanza para estos tests, que no ejercitan la independencia de los dos espacios de hash (eso
    // lo cubre SyncPlannerTests y RunAsync_CuatroCiclosSeguidosSinCambiosRemotos... más abajo).
    private static SyncBaselineItem AsBaseline(SyncItemState state, string? remoteHash = null) =>
        new(state.Id, state.Kind, state.ContentHash, remoteHash ?? state.ContentHash, state.UpdatedAt, state.Deleted);

    public SyncEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "at_tests_" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_root, ".synccache", "index.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static SyncEngine BuildEngine(string root, string dbPath, FakeHandler apiHandler, FakeHandler? uploadHandler = null)
    {
        var apiClient = new SyncApiClient(new HttpClient(apiHandler), "https://app.vercel.app");
        var uploadHttp = new HttpClient(uploadHandler ?? apiHandler);
        return new SyncEngine(
            apiClient, uploadHttp, new SyncIndex(dbPath), new LocalScanner(), new RemoteMapper(), new SyncPlanner(),
            root, "https://app.vercel.app");
    }

    private const string EmptyPull = """{"serverTime":"2026-07-06T00:00:00Z","projects":[],"transcriptions":[]}""";

    // ---- Ciclo normal: mix de push y pull upserts --------------------------

    [Fact]
    public async Task RunAsync_CicloNormal_PusheaCambioLocalYBajaProyectoNuevo()
    {
        // Local: proyecto "Trabajo" con una transcripción ya sincronizada en la baseline.
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "texto viejo");

        var scanner = new LocalScanner();
        var initialSnapshot = scanner.ScanDetailed(_root);
        var initialScan = initialSnapshot.Items;
        var projectId = initialScan.First(kv => kv.Value.Kind == SyncItemKind.Project).Key;
        var transcriptionId = initialScan.First(kv => kv.Value.Kind == SyncItemKind.Transcription).Key;

        var index = new SyncIndex(_dbPath);
        // ADR-06: sin idMap, el próximo scan (el que RunAsync hace por su cuenta) acuñaría ids
        // NUEVOS y aleatorios para "Trabajo" -- no calzarían con esta baseline. Se persisten los
        // pares acuñados en ESTE scan (MintedIds) para simular "ya se sincronizó antes" -- el mismo
        // registro que un ciclo real de RunAsync ya deja (Task 1.6).
        index.SaveIdMap(initialSnapshot.MintedIds);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(initialScan[projectId]),
            [transcriptionId] = AsBaseline(initialScan[transcriptionId]), // hash coincide con "texto viejo" -> sin cambios todavía
        });

        // El usuario edita el texto localmente -> debería generar un PushUpsert de transcripción.
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "texto nuevo editado");

        // El remoto tiene un proyecto nuevo "Personal" que el cliente nunca vio -> PullUpsert.
        var pullJson = """
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[{"id":"remote-personal","name":"Personal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[]}
            """;

        var apiHandler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var apiClient = new SyncApiClient(new HttpClient(apiHandler), "https://app.vercel.app");
        var engine = new SyncEngine(
            apiClient, new HttpClient(apiHandler), index, scanner, new RemoteMapper(), new SyncPlanner(),
            _root, "https://app.vercel.app");

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // Se pusheó la edición local.
        var pushBody = apiHandler.Bodies[apiHandler.Requests.FindIndex(r => r.Method == HttpMethod.Post)];
        Assert.Contains("texto nuevo editado", pushBody);

        // Se bajó el proyecto remoto nuevo como carpeta local.
        Assert.True(Directory.Exists(Path.Combine(ws.AudiosPath, "Personal")));

        // La baseline quedó actualizada con ambos ids, reflejando el nuevo texto pusheado.
        var newBaseline = index.LoadBaseline();
        Assert.True(newBaseline.ContainsKey("remote-personal"));
        var rescan = scanner.ScanDetailed(_root, index.LoadIdMap()).Items;
        Assert.Equal(rescan[transcriptionId].ContentHash, newBaseline[transcriptionId].LastLocalHash);
    }

    // ---- Task 1.5/1.6 (ADR-06): un id acuñado sobrevive un push que falla ---------------------
    // Un item local NUEVO (sin idOverride) se acuña con un UUIDv4 aleatorio en el primer scan del
    // ciclo (Task 1.2). Ese id se persiste en SyncIdMap YA en este mismo ciclo (Task 1.6), incluso
    // si el servidor rechaza el push -- son identidad, no estado de sync (ADR-06.3). Re-acuñar en
    // el ciclo siguiente duplicaría el item en el próximo push exitoso (Riesgo #1 del design).

    [Fact]
    public async Task RunAsync_IdAcunadoEnPrimerScan_SobreviveUnPushQueFallaYSeReusaElMismoIdElCicloSiguiente()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "hola");

        // El servidor rechaza el push con un error genérico (no el patrón de borrado en cascada) --
        // sin relación con la identidad, solo para confirmar que el rechazo no impide que el id
        // acuñado se guarde igual (ver ReconcilePushResponse: revierte newBaseline, nunca newIdOverrides).
        var pushErrorJson = """{"serverTime":"2026-07-28T00:00:00Z","ok":false,"errors":["error genérico del servidor"]}""";
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json(pushErrorJson));
        var engine = BuildEngine(_root, _dbPath, handler);

        var first = await engine.RunAsync("token-123");
        Assert.Equal(SyncOutcome.Completed, first.Outcome);

        var index = new SyncIndex(_dbPath);
        var projectPathKey = LocalScanner.ProjectPathKey("Trabajo");
        var idMapAfterFirstCycle = index.LoadIdMap();
        Assert.True(
            idMapAfterFirstCycle.ContainsKey(projectPathKey),
            "el id acuñado del proyecto nuevo debe persistirse aunque el push haya sido rechazado");
        var mintedProjectId = idMapAfterFirstCycle[projectPathKey];

        // La baseline SÍ se revirtió (el push falló, ver ReconcilePushResponse) -- pero el id-map no.
        Assert.False(index.LoadBaseline().ContainsKey(mintedProjectId));

        var second = await engine.RunAsync("token-123");
        Assert.Equal(SyncOutcome.Completed, second.Outcome);

        var idMapAfterSecondCycle = index.LoadIdMap();
        Assert.Equal(mintedProjectId, idMapAfterSecondCycle[projectPathKey]);

        // El segundo ciclo reintenta el push del MISMO id -- nunca uno nuevo/duplicado.
        Assert.Contains(second.Actions, a => a.Id == mintedProjectId && a.Kind == SyncItemKind.Project);
    }

    // ---- Task 2.6 (ADR-07c/g): el push manda base_version desde la baseline -------------------

    [Fact]
    public async Task RunAsync_PushUpsertDeItemYaConocido_MandaBaseVersionDeLaBaseline()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "texto viejo");

        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root);
        var projectId = snapshot.Projects.Values.Single().Id;
        var transcriptionId = snapshot.Transcriptions.Values.Single().Id;

        var index = new SyncIndex(_dbPath);
        index.SaveIdMap(snapshot.MintedIds);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(snapshot.Items[projectId]) with { LastRemoteVersion = 9 },
            [transcriptionId] = AsBaseline(snapshot.Items[transcriptionId]) with { LastRemoteVersion = 4 },
        });

        // Edición local -> dispara un PushUpsert de transcripción; el proyecto no cambió.
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "texto nuevo");

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        await engine.RunAsync("token-123");

        var pushBody = handler.Bodies[handler.Requests.FindIndex(r => r.Method == HttpMethod.Post)];
        Assert.Contains("\"base_version\":4", pushBody);
    }

    [Fact]
    public async Task RunAsync_PushUpsertDeItemNuevo_NoMandaBaseVersion()
    {
        // Un ítem que NUNCA se sincronizó (sin entrada en baseline) no tiene base_version que
        // comparar -- se omite del JSON (ADR-07g): un 0 falso haría que el servidor lo tratara como
        // "el cliente creía tener la versión inicial", lo que no es cierto para un alta.
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "texto nuevo local");

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        await engine.RunAsync("token-123");

        var pushBody = handler.Bodies[handler.Requests.FindIndex(r => r.Method == HttpMethod.Post)];
        Assert.DoesNotContain("base_version", pushBody);
    }

    // ---- Freno anti-borrado-masivo -----------------------------------------

    private async Task<(SyncIndex index, FakeHandler handler, string projectId, string transcriptionId)> SeedMassDeletionScenario()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "hola");

        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root);
        var scan = snapshot.Items;
        var projectId = scan.First(kv => kv.Value.Kind == SyncItemKind.Project).Key;
        var transcriptionId = scan.First(kv => kv.Value.Kind == SyncItemKind.Transcription).Key;

        var index = new SyncIndex(_dbPath);
        // ADR-06: persiste los ids acuñados en este scan -- si no, el scan interno de RunAsync
        // acuña ids random NUEVOS para "Trabajo" que ya no calzan con projectId/transcriptionId
        // (los que la baseline y el pull de abajo referencian explícitamente).
        index.SaveIdMap(snapshot.MintedIds);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(scan[projectId]),
            [transcriptionId] = AsBaseline(scan[transcriptionId]),
        });

        // El remoto marca AMBOS items como borrados (deleted_at) -> 2 de 2 en baseline = 100% > 40%.
        var pullJson = $$"""
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[{"id":"{{projectId}}","name":"Trabajo","updated_at":"2026-07-06T01:00:00Z","deleted_at":"2026-07-06T01:00:00Z"}],
             "transcriptions":[{"id":"{{transcriptionId}}","text":"hola","updated_at":"2026-07-06T01:00:00Z","deleted_at":"2026-07-06T01:00:00Z"}]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));

        return await Task.FromResult((index, handler, projectId, transcriptionId));
    }

    [Fact]
    public async Task RunAsync_BorradoMasivo_SinConfirmar_AbortaSinEjecutarNada()
    {
        var (index, handler, projectId, _) = await SeedMassDeletionScenario();
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.ConfirmationPending, result.Outcome);
        Assert.Equal(2, result.DeleteCount);
        Assert.NotNull(result.Message);

        // No se ejecutó ningún borrado: la carpeta del proyecto sigue intacta.
        Assert.True(Directory.Exists(Path.Combine(_root, "audios", "Trabajo")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".papelera")));

        // No se llamó a push (no se le avisó nada al servidor todavía).
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);

        // La baseline no se tocó.
        var baseline = index.LoadBaseline();
        Assert.Equal(2, baseline.Count);
        Assert.False(baseline[projectId].Deleted);
    }

    [Fact]
    public async Task RunAsync_BorradoMasivo_ConConfirmacionExplicita_EjecutaYMuevePapelera()
    {
        var (index, handler, projectId, transcriptionId) = await SeedMassDeletionScenario();
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123", forceConfirmDeletes: true);

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // Son PullDelete (el remoto ya los tenía borrados) -> se mueve la carpeta local a .papelera.
        Assert.False(Directory.Exists(Path.Combine(_root, "audios", "Trabajo")));
        Assert.True(Directory.Exists(Path.Combine(_root, ".papelera")));
        var papeleraBuckets = Directory.GetDirectories(Path.Combine(_root, ".papelera"));
        Assert.Contains(papeleraBuckets, b => Directory.Exists(Path.Combine(b, "audios")));

        // Baseline refleja el borrado.
        var newBaseline = index.LoadBaseline();
        Assert.True(newBaseline[projectId].Deleted);
        Assert.True(newBaseline[transcriptionId].Deleted);
    }

    // ---- Regresión: ausencia local NO debe sintetizar un borrado (bugfix pérdida de datos) --

    [Fact]
    public async Task RunAsync_AusenciaLocal_NoGeneraPushDelete()
    {
        // Reproduce el escenario del bug arreglado en MergeWithLocalTombstones: una transcripción
        // que ya está en la baseline (se sincronizó antes) pero cuyo archivo de AUDIO ya no está
        // en disco (p.ej. porque un PullUpsert anterior solo escribió el .txt, sin bajar el blob
        // de audio — ver LocalScanner). LocalScanner enumera transcripciones por archivo de audio,
        // así que esta no aparece en el scan siguiente. Con la síntesis de tombstones activa (el
        // bug), esa ausencia se interpretaba como "borrado local" y generaba un PushDelete que
        // vaciaba la nube. Con el fix, la ausencia se trata como "sin cambios".
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "nota.mp3"), "audio-bytes");
        File.WriteAllText(ws.TranscriptPathFor("nota.mp3"), "texto original");

        var scanner = new LocalScanner();
        var initialScan = scanner.Scan(_root);
        var transcriptionId = initialScan.First(kv => kv.Value.Kind == SyncItemKind.Transcription).Key;

        var index = new SyncIndex(_dbPath);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [transcriptionId] = AsBaseline(initialScan[transcriptionId]), // ya sincronizada, Deleted=false
        });

        // Simula el gap de descarga de audio: el .mp3 desaparece de disco, pero el id sigue en
        // la baseline. El próximo scan local YA NO la va a encontrar.
        File.Delete(Path.Combine(ws.AudiosPath, "nota.mp3"));
        Assert.Empty(scanner.Scan(_root)); // confirma que el scan post-borrado ya no la ve

        // El remoto no reporta cambios para este id (pull incremental: nada cambió del lado
        // servidor desde el último sync) -> no hay ninguna señal real de borrado.
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // Ninguna acción de borrado: la ausencia local no se propagó a un PushDelete.
        Assert.DoesNotContain(result.Actions, a => a.Type == SyncActionType.PushDelete);
        Assert.Equal(0, result.DeleteCount);
        Assert.Empty(result.Actions);

        // No se llamó a push: no se le avisó ningún borrado al servidor.
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);

        // La baseline sigue teniendo la transcripción como NO borrada.
        var newBaseline = index.LoadBaseline();
        Assert.True(newBaseline.ContainsKey(transcriptionId));
        Assert.False(newBaseline[transcriptionId].Deleted);
    }

    [Fact]
    public async Task RunAsync_PrimerSyncCarpetaVaciaBaselineVaciaConNubeConDatos_SoloPullUpsertsSinPushDelete()
    {
        // Escenario bonus: primer sync real (nunca hubo baseline) con la carpeta local recién
        // creada, todavía vacía. La nube tiene un proyecto y una transcripción. El resultado
        // esperado es bajar todo (PullUpsert) y jamás generar un PushDelete: no hay nada local
        // que "borrar" porque nunca hubo nada local en primer lugar.
        var index = new SyncIndex(_dbPath);

        var pullJson = """
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[{"id":"remote-personal","name":"Personal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"remote-t1","project_id":"remote-personal","title":"Nota","audio_name":"nota.mp3","text":"hola","updated_at":"2026-07-06T00:00:00Z"}]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.DeleteCount);
        Assert.NotEmpty(result.Actions);
        Assert.All(result.Actions, a => Assert.Equal(SyncActionType.PullUpsert, a.Type));

        // No se llamó a push: todo lo que pasó fue traer datos de la nube.
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);

        // Se bajó el proyecto a disco.
        Assert.True(Directory.Exists(Path.Combine(_root, "audios", "Personal")));

        // La baseline quedó poblada con ambos ids, ninguno marcado como borrado.
        var newBaseline = index.LoadBaseline();
        Assert.Equal(2, newBaseline.Count);
        Assert.All(newBaseline.Values, s => Assert.False(s.Deleted));
    }

    // ---- Fix de raíz: PullUpsert con audio_url_signed baja el audio, no solo el .txt --------

    [Fact]
    public async Task RunAsync_PullUpsertConAudioUrlSigned_DescargaYGuardaElAudio()
    {
        // Complementa el bugfix de MergeWithLocalTombstones (que solo evita el síntoma: no
        // sintetizar tombstones por ausencia local). Esto arregla la causa raíz: si el backend
        // manda audio_url_signed en el pull, el audio se descarga junto con el .txt, así el
        // próximo scan local encuentra la transcripción completa y nunca la ve como ausente.
        var index = new SyncIndex(_dbPath);
        const string audioUrl = "https://storage.example.com/signed/nota.mp3?token=abc123";
        var audioBytes = Encoding.UTF8.GetBytes("contenido-de-audio-fake");

        var pullJson = $$"""
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[{"id":"remote-personal","name":"Personal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"remote-t1","project_id":"remote-personal","title":"Nota","audio_name":"nota.mp3","audio_url_signed":"{{audioUrl}}","text":"hola","updated_at":"2026-07-06T00:00:00Z"}]}
            """;

        // El handler distingue la descarga del audio (va a la URL firmada, no al backend) del
        // resto de las llamadas de SyncApiClient (pull/push contra "https://app.vercel.app").
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.ToString() == audioUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audioBytes) };
            return req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}""");
        });
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // Se bajó el audio (no solo el .txt) a la ruta esperada dentro del proyecto "Personal".
        var ws = Workspace.OpenOrCreate(_root);
        var audioPath = ws.AudioPathFor("nota.mp3", "Personal");
        Assert.True(File.Exists(audioPath));
        Assert.Equal(audioBytes, await File.ReadAllBytesAsync(audioPath));

        // El .txt también se escribió, como siempre.
        Assert.True(File.Exists(ws.TranscriptPathFor("nota.mp3", "Personal")));
    }

    // ---- Bug real reportado: transcripciones de la web "invisibles" en desktop ---------------
    // Causa raíz: si audio_url_signed venía en el pull pero la descarga fallaba (red, URL vencida,
    // status no-éxito), DownloadAudioBestEffortAsync lo tragaba en silencio Y el código YA marcaba
    // newBaseline[id] = remote[id] igual, sin importar si el audio se bajó o no. Como el hash
    // remoto de una transcripción (Title/AudioName/Text/ProjectId, ver RemoteMapper) NO incluye
    // audio_url_signed, el próximo ciclo veía "sin cambios" (mismo hash ya en baseline) y NUNCA
    // reintentaba. El .txt quedaba huérfano en disco (sin audio), y como LocalScanner enumera
    // transcripciones por archivo de AUDIO (ver MergeWithLocalTombstones), la transcripción
    // desaparecía de la UI para siempre -- ni el sync automático (timer 60s) ni "Sincronizar
    // ahora" manual la traían, porque el ciclo ya la daba por sincronizada.
    //
    // Fix v1.0.11 (histórico): dejar de marcarla sincronizada para forzar un reintento indefinido
    // en el próximo ciclo. Fix 2026-07-08 (este, ver ExecutePullTranscriptionUpsertAsync): ese
    // reintento indefinido dejó de ser seguro una vez que Workspace.ListAudiosIn empezó a listar
    // .txt huérfanos como transcripciones "solo texto" (pedido explícito: deben verse) -- mientras
    // esperaba el reintento, ese mismo .txt podía generar un id local NUEVO en cada scan (no había
    // override registrado todavía), con riesgo real de duplicarse en el próximo push. Ahora "sin
    // audio_url_signed" y "la descarga falló" se tratan igual: se guarda como solo-texto y se
    // marca sincronizada de una, con identidad estable.

    [Fact]
    public async Task RunAsync_PullUpsertConAudioUrlSignedQueFallaLaDescarga_QuedaSoloTextoYMarcadaSincronizada()
    {
        var index = new SyncIndex(_dbPath);
        const string audioUrl = "https://storage.example.com/signed/nota.mp3?token=abc123";

        var pullJson = $$"""
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[{"id":"remote-personal","name":"Personal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"remote-t1","project_id":"remote-personal","title":"Nota","audio_name":"nota.mp3","audio_url_signed":"{{audioUrl}}","text":"hola","updated_at":"2026-07-06T00:00:00Z"}]}
            """;

        // La descarga del audio falla (URL vencida / 404).
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.ToString() == audioUrl)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}""");
        });
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // El .txt se escribe igual (no se pierde el texto ya bajado)...
        var ws = Workspace.OpenOrCreate(_root);
        Assert.True(File.Exists(ws.TranscriptPathFor("nota.mp3", "Personal")));
        // ...pero el audio NO se bajó.
        Assert.False(File.Exists(ws.AudioPathFor("nota.mp3", "Personal")));

        // Se marca sincronizada YA (decisión 2026-07-08: ya no se reintenta indefinidamente).
        var newBaseline = index.LoadBaseline();
        Assert.True(newBaseline.ContainsKey("remote-t1"));

        // AudioDownloadFailures sigue contando el intento fallido, solo a fines diagnósticos (ya
        // no bloquea que se marque sincronizada -- ver SyncCoordinator/logs/sync-*.log).
        Assert.Equal(1, result.PulledProjectsCount);
        Assert.Equal(1, result.PulledTranscriptionsCount);
        Assert.Equal(1, result.AudioDownloadFailures);

        // Visible como solo-texto para LocalScanner (lo que alimenta la UI), con id estable.
        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root, index.LoadIdMap());
        var transcription = Assert.Single(snapshot.Transcriptions.Values);
        Assert.Equal("remote-t1", transcription.Id);
    }

    // ---- Bug real reportado 2026-07-21: el sync pisa el audio ORIGINAL local con el comprimido
    // de la nube -------------------------------------------------------------------------------
    // El usuario arrastra un WAV de ~20MB; el sync lo sube comprimido (opus, ~2MB) a la nube para
    // que Groq lo transcriba. Hasta acá todo bien -- pero en el siguiente pull, el backend manda
    // audio_url_signed apuntando a ESE MISMO comprimido, y DownloadAudioBestEffortAsync lo bajaba
    // y pisaba (File.Move overwrite:true) el WAV original de 20MB con la copia de ~2MB: pérdida de
    // calidad irreversible. Fix: si YA hay un audio local en audioPath, es el original del usuario
    // (mejor calidad que lo que guarda la nube) -- nunca se pisa. Solo se baja el comprimido cuando
    // NO hay audio local (p.ej. un equipo nuevo pulleando todo de cero, ver el test de arriba
    // RunAsync_PullUpsertConAudioUrlSigned_DescargaYGuardaElAudio, que sigue cubriendo ese caso).

    [Fact]
    public async Task RunAsync_PullUpsertConAudioLocalYaExistente_NoLoPisaConElComprimidoDeLaNube()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Personal");

        // Audio ORIGINAL del usuario ya presente en disco (simula el WAV de alta calidad que
        // arrastró y que el sync ya sincronizó en un ciclo anterior).
        var originalBytes = Encoding.UTF8.GetBytes("contenido-ORIGINAL-wav-alta-calidad-del-usuario");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Personal"));
        File.WriteAllText(ws.TranscriptPathFor("nota.mp3", "Personal"), "hola");
        File.WriteAllBytes(ws.AudioPathFor("nota.mp3", "Personal"), originalBytes);

        var index = new SyncIndex(_dbPath);
        index.SaveIdMap(new Dictionary<string, string>
        {
            [LocalScanner.ProjectPathKey("Personal")] = "remote-personal",
            [LocalScanner.TranscriptionPathKey("Personal", "nota.mp3")] = "remote-t1",
        });

        var scanner = new LocalScanner();
        var initialScan = scanner.ScanDetailed(_root, index.LoadIdMap());
        var projectId = initialScan.Projects.Values.Single().Id;
        var transcriptionId = initialScan.Transcriptions.Values.Single().Id;

        // Baseline "ya sincronizada" salvo por el remote hash: se fuerza distinto al que va a
        // calcular el RemoteMapper para el pull de abajo (que SÍ trae audio_url_signed), así el
        // planner detecta "cambio remoto" (audio_url_signed recién apareció) sin que el LOCAL
        // también luzca cambiado -- un PullUpsert limpio, igual que
        // RunAsync_AudioUrlSignedAppearsOnSecondPull_SelfHealsAndDownloadsAudio más arriba.
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(initialScan.Projects[projectId].State),
            [transcriptionId] = AsBaseline(
                initialScan.Transcriptions[transcriptionId].State,
                remoteHash: "seed-hash-antes-de-que-exista-audio-en-la-nube"),
        });

        const string audioUrl = "https://storage.example.com/signed/nota.mp3?token=abc123";
        var compressedBytes = Encoding.UTF8.GetBytes("comprimido-opus-de-la-nube-mucha-menor-calidad");
        var pullJson = $$"""
            {"serverTime":"2026-07-21T00:00:00Z",
             "projects":[{"id":"remote-personal","name":"Personal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"remote-t1","project_id":"remote-personal","title":"Nota","audio_name":"nota.mp3","audio_url_signed":"{{audioUrl}}","text":"hola","updated_at":"2026-07-21T00:00:00Z"}]}
            """;

        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.ToString() == audioUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(compressedBytes) };
            return req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}""");
        });
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        // El PullUpsert de la transcripción efectivamente se ejecutó (para este id, no un duplicado).
        Assert.Contains(result.Actions, a => a.Id == "remote-t1" && a.Type == SyncActionType.PullUpsert);

        // EL AUDIO ORIGINAL NO SE TOCÓ: sigue teniendo los bytes de alta calidad del usuario, NO
        // los del comprimido que mandó la nube.
        var audioPath = ws.AudioPathFor("nota.mp3", "Personal");
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(audioPath));
        Assert.NotEqual(compressedBytes, await File.ReadAllBytesAsync(audioPath));
    }

    // ---- Transcripciones SOLO TEXTO (bug: invisibles para siempre en desktop, 2026-07-08) ----
    // Causa raíz real (confirmada leyendo LocalScanner/Workspace): una transcripción remota sin
    // audio_url_signed YA se marcaba sincronizada (fix anterior, v1.0.11), pero LocalScanner
    // enumeraba transcripciones SOLO por archivo de audio (Workspace.ListAudiosIn), así que el
    // .txt que se escribía acá nunca aparecía en la UI ni en el árbol de proyectos.

    [Fact]
    public async Task RunAsync_PullUpsertSinAudioUrlSigned_QuedaVisibleParaLocalScanner()
    {
        var index = new SyncIndex(_dbPath);

        var pullJson = """
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[{"id":"remote-personal","name":"Personal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"remote-t1","project_id":"remote-personal","title":"Nota","audio_name":"nota.mp3","text":"solo texto","updated_at":"2026-07-06T00:00:00Z"}]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        var ws = Workspace.OpenOrCreate(_root);
        Assert.True(File.Exists(ws.TranscriptPathFor("nota.mp3", "Personal")));

        // Marcada sincronizada de una: sin audio que reintentar (audio_url_signed nunca vino).
        var newBaseline = index.LoadBaseline();
        Assert.True(newBaseline.ContainsKey("remote-t1"));
        Assert.Equal(0, result.AudioDownloadFailures);

        // Clave del fix: LocalScanner (lo que alimenta la UI y el árbol de proyectos) la ve, aunque
        // no exista ningún archivo de audio en disco.
        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root, index.LoadIdMap());
        var transcription = Assert.Single(snapshot.Transcriptions.Values);
        Assert.Equal("remote-t1", transcription.Id);
        Assert.False(transcription.State.Deleted);
        Assert.Equal("solo texto", transcription.Text);
    }

    [Fact]
    public async Task RunAsync_SegundoCicloConTranscripcionSoloTexto_MantieneElMismoIdSinDuplicar()
    {
        // Regresión de identidad: sin el idOverride adicional (por STEM del .txt, ver
        // SyncEngine.ExecutePullTranscriptionUpsertAsync), el segundo scan local generaría un id
        // NUEVO para este ítem (no puede reconstruir la extensión original de audio_name a partir
        // de un .txt huérfano) y el sync la trataría como un ítem local nuevo -- duplicándola en el
        // próximo push.
        var index = new SyncIndex(_dbPath);

        var pullJson = """
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[{"id":"remote-personal","name":"Personal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"remote-t1","project_id":"remote-personal","title":"Nota","audio_name":"nota.mp3","text":"solo texto","updated_at":"2026-07-06T00:00:00Z"}]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        await engine.RunAsync("token-123");

        var second = await engine.RunAsync("token-123");

        Assert.DoesNotContain(second.Actions, a =>
            a.Kind == SyncItemKind.Transcription && a.Type == SyncActionType.PushUpsert && a.Id != "remote-t1");
    }

    [Fact]
    public async Task RunAsync_TranscripcionSoloTexto_AgregaDiagnosticoDescriptivo()
    {
        var index = new SyncIndex(_dbPath);
        var pullJson = """
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[],
             "transcriptions":[{"id":"remote-t1","title":"Nota","audio_name":"nota.mp3","text":"solo texto","updated_at":"2026-07-06T00:00:00Z"}]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.NotNull(result.Diagnostics);
        Assert.Contains(result.Diagnostics!, d => d.Contains("sin audio_url_signed"));
    }

    [Fact]
    public async Task RunAsync_ProjectIdNoResuelto_AgregaDiagnosticoYAlojaEnGeneral()
    {
        var index = new SyncIndex(_dbPath);
        var pullJson = """
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[],
             "transcriptions":[{"id":"remote-t1","project_id":"proyecto-desconocido","title":"Nota","audio_name":"nota.mp3","text":"hola","updated_at":"2026-07-06T00:00:00Z"}]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        var ws = Workspace.OpenOrCreate(_root);
        Assert.True(File.Exists(ws.TranscriptPathFor("nota.mp3")));
        Assert.Contains(result.Diagnostics!, d => d.Contains("proyecto-desconocido") && d.Contains("General"));
    }

    // ---- Fix 2026-07-08 (v1.0.16): orphaned text-only items self-heal once the backend reports
    // audio_url_signed on a later pull -----------------------------------------------------------
    // Regression test for the direct report: cycle 1 has NO audio_url_signed for an item (e.g. a
    // transient createSignedUrl failure on the backend), so it is saved text-only. Cycle 2 has a
    // valid audio_url_signed for the SAME item. Before the RemoteMapper fix, ContentHash ignored
    // audio presence, so the hash stayed identical across cycles and SyncPlanner never produced a
    // new action for this id -- the audio was orphaned forever. With the fix, the hash changes
    // exactly once (no-URL -> URL), SyncPlanner emits a PullUpsert, and the audio download is
    // attempted again.

    [Fact]
    public async Task RunAsync_AudioUrlSignedAppearsOnSecondPull_SelfHealsAndDownloadsAudio()
    {
        var index = new SyncIndex(_dbPath);
        const string audioUrl = "https://storage.example.com/signed/nota.mp3?token=abc123";
        var audioBytes = Encoding.UTF8.GetBytes("contenido-de-audio-fake");

        var pullJsonNoAudio = """
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[{"id":"remote-personal","name":"Personal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"remote-t1","project_id":"remote-personal","title":"Nota","audio_name":"nota.mp3","text":"hola","updated_at":"2026-07-06T00:00:00Z"}]}
            """;
        // updated_at is set far in the future defensively (not load-bearing for correctness
        // anymore -- kept only as extra insurance against flakiness). CORRECTION (2026-07-10): a
        // previous version of this comment claimed cycle 2 was a manufactured local-vs-remote
        // CONFLICT that "self-stabilized after one echo push" because LocalScanner/RemoteMapper hash
        // a pulled item over DIFFERENT field sets and the baseline only tracked ONE hash -- so the
        // very next local rescan of an already-pulled item always looked "locally changed" too. That
        // was WRONG: it was not a self-stabilizing quirk, it was the perpetual-oscillation bug (see
        // changelog 2026-07-10, SyncPlanner/SyncEngine.BuildBaselineEntry) -- under the old model
        // this "conflict" reappeared EVERY cycle forever, not just once. With the two-hash baseline
        // fix, LastLocalHash/LastRemoteHash are anchored independently, so cycle 2 correctly sees
        // "remote changed (hasAudioSigned flipped), local unchanged" -- a clean PullUpsert, no
        // conflict tie-break involved at all (asserted below via Reason == "cambio remoto").
        var pullJsonWithAudio = $$"""
            {"serverTime":"2026-07-07T00:00:00Z",
             "projects":[{"id":"remote-personal","name":"Personal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"remote-t1","project_id":"remote-personal","title":"Nota","audio_name":"nota.mp3","audio_url_signed":"{{audioUrl}}","text":"hola","updated_at":"2099-01-01T00:00:00Z"}]}
            """;

        var pullCallCount = 0;
        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.ToString() == audioUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audioBytes) };
            if (req.Method == HttpMethod.Get)
            {
                pullCallCount++;
                return Json(pullCallCount == 1 ? pullJsonNoAudio : pullJsonWithAudio);
            }
            return Json("""{"ok":true}""");
        });
        var engine = BuildEngine(_root, _dbPath, handler);

        var first = await engine.RunAsync("token-123");

        var ws = Workspace.OpenOrCreate(_root);
        Assert.Equal(SyncOutcome.Completed, first.Outcome);
        Assert.True(File.Exists(ws.TranscriptPathFor("nota.mp3", "Personal")));
        Assert.False(File.Exists(ws.AudioPathFor("nota.mp3", "Personal")));

        var second = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, second.Outcome);

        // The hash change (no-URL -> URL) is what lets SyncPlanner detect the item changed, so it
        // re-runs the PullUpsert branch for this same id -- not a new/duplicate id.
        var t1Action = Assert.Single(second.Actions, a => a.Id == "remote-t1" && a.Kind == SyncItemKind.Transcription);
        Assert.Equal(SyncActionType.PullUpsert, t1Action.Type);
        // Bugfix 2026-07-10 regression: this is a clean "remote changed only" detection, NOT a
        // manufactured conflict resolved by last-write-wins (see corrected comment above).
        Assert.Equal("cambio remoto", t1Action.Reason);

        // The previously orphaned audio is now downloaded and saved.
        var audioPath = ws.AudioPathFor("nota.mp3", "Personal");
        Assert.True(File.Exists(audioPath));
        Assert.Equal(audioBytes, await File.ReadAllBytesAsync(audioPath));
    }

    // ---- Fix 2026-07-09 (v1.0.23): pull-upsert wiped the local project color -------------------
    // Confirmed live bug: ExecutePullProjectUpsert rebuilds the project via Workspace.CreateProject
    // (Color always null, the server-side DTO doesn't carry color at all) and persists it with
    // SaveProjectMeta, so ANY remote-side change to an already-local project (e.g. its Description
    // edited on the web) silently wiped out the color the user had picked locally, on every
    // auto-sync cycle (every 60s) or manual "Sincronizar ahora". Reproduced live with project
    // "grabado" at C:\Transcriber\audios\grabado\_proyecto.json: color set to "indigo" by hand,
    // reverted to null within seconds of the app's own auto-sync. Fix: read back the color already
    // on disk (Workspace.ReadProjectColor) before SaveProjectMeta re-writes _proyecto.json.

    [Fact]
    public async Task RunAsync_PullUpsertDeProyectoYaLocalConColor_PreservaElColorEnDisco()
    {
        // Proyecto ya local (nunca antes sincronizado) con un color elegido por el usuario.
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("grabado");
        project.Color = "indigo";
        ws.SaveProjectMeta(project);
        Assert.Equal("indigo", ws.ReadProjectColor(project.FolderPath)); // precondición

        var scanner = new LocalScanner();
        var initialScan = scanner.Scan(_root);
        var projectId = initialScan.First(kv => kv.Value.Kind == SyncItemKind.Project).Key;

        var index = new SyncIndex(_dbPath);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(initialScan[projectId]),
        });

        // El remoto reporta un cambio para ESE MISMO proyecto (descripción editada desde la web) --
        // color no viaja en el DTO, el server no sabe que existe. Esto dispara exactamente el
        // camino de ExecutePullProjectUpsert sobre un proyecto que YA existe localmente.
        var pullJson = $$"""
            {"serverTime":"2026-07-06T01:00:00Z",
             "projects":[{"id":"{{projectId}}","name":"grabado","description":"editado desde la web","updated_at":"2026-07-06T01:00:00Z"}],
             "transcriptions":[]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Contains(result.Actions, a => a.Id == projectId && a.Type == SyncActionType.PullUpsert);

        // La descripción SÍ se actualizó (el pull-upsert corrió de verdad)...
        var reloaded = ws.ListProjects().Single(p => p.Name == "grabado");
        Assert.Equal("editado desde la web", reloaded.Description);
        // ...pero el color local, que el server nunca conoció, se preservó en vez de perderse.
        Assert.Equal("indigo", reloaded.Color);
        Assert.Equal("indigo", ws.ReadProjectColor(project.FolderPath));
    }

    // ---- Fix 2026-07-10 (HIGH): oscilación perpetua de sync (modelo de dos hashes) -------------
    // Causa raíz: la reconciliación de 3 vías comparaba UN solo ContentHash por ítem, pero local y
    // remoto lo calculan sobre campos DISJUNTOS (ver LocalScanner.ScanDetailed/RemoteMapper.Map) --
    // nunca coinciden, así que no había punto fijo: cada ciclo alternaba PushUpsert/PullUpsert para
    // siempre aunque el remoto nunca cambiara (re-descarga de audio, re-escritura de .txt, refresh
    // de UI y status pegado en "N acciones aplicadas" perpetuamente). Fix: SyncBaselineItem ancla
    // LastLocalHash/LastRemoteHash por separado (ver SyncPlanner/SyncEngine.BuildBaselineEntry).

    [Fact]
    public async Task RunAsync_CuatroCiclosSeguidosSinCambiosRemotos_DesdeElCicloTresNoGeneraAcciones()
    {
        // Corre 4 ciclos contra un remoto que NUNCA cambia (mismo pull, mismo handler, mismo texto y
        // audio) y afirma que desde el ciclo 3 en adelante no se genera NINGUNA acción -- el ciclo 1
        // baja todo (primer sync), y de ahí en más el sync tiene que quedar en punto fijo.
        const string audioUrl = "https://storage.example.com/signed/reunion.mp3?token=abc123";
        var audioBytes = Encoding.UTF8.GetBytes("contenido-de-audio-fake");

        var pullJson = $$"""
            {"serverTime":"2026-07-06T00:00:00Z",
             "projects":[{"id":"remote-trabajo","name":"Trabajo","description":"reunión semanal","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"remote-t1","project_id":"remote-trabajo","title":"Nota","audio_name":"reunion.mp3","audio_url_signed":"{{audioUrl}}","text":"hola equipo","updated_at":"2026-07-06T00:00:00Z"}]}
            """;

        var handler = new FakeHandler(req =>
        {
            if (req.RequestUri!.ToString() == audioUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audioBytes) };
            return req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}""");
        });
        var engine = BuildEngine(_root, _dbPath, handler);

        var cycle1 = await engine.RunAsync("token-123");
        var cycle2 = await engine.RunAsync("token-123");
        var cycle3 = await engine.RunAsync("token-123");
        var cycle4 = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, cycle1.Outcome);
        Assert.NotEmpty(cycle1.Actions); // primer sync: baja todo
        Assert.Equal(SyncOutcome.Completed, cycle2.Outcome);
        Assert.Equal(SyncOutcome.Completed, cycle3.Outcome);
        Assert.Equal(SyncOutcome.Completed, cycle4.Outcome);

        // Nunca hay un cambio local legítimo que subir en este escenario -- ningún POST al backend.
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);

        // Punto fijo: desde el ciclo 3 en adelante, cero acciones (antes del fix, oscilaba para
        // siempre -- ver comentario de cabecera).
        Assert.Empty(cycle3.Actions);
        Assert.Empty(cycle4.Actions);
    }

    // ---- Fix 2026-07-10 (MEDIUM): rename remoto de proyecto preserva el color y no huérfana la
    // carpeta vieja --------------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_PullUpsertDeProyectoRenombradoDesdeLaWeb_PreservaColorYMueveLaCarpetaVieja()
    {
        // Antes: un rename remoto (mismo id, nombre nuevo) resolvía SIEMPRE por nombre --
        // CreateProject(nombreNuevo) creaba una carpeta NUEVA vacía (color=null) y la carpeta vieja
        // (con color y audios) quedaba huérfana. El fix mueve la carpeta existente en vez de crear
        // una nueva.
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("grabado");
        project.Color = "indigo";
        ws.SaveProjectMeta(project);
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");

        var scanner = new LocalScanner();
        var initialScan = scanner.Scan(_root);
        var projectId = initialScan.First(kv => kv.Value.Kind == SyncItemKind.Project).Key;

        var index = new SyncIndex(_dbPath);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(initialScan[projectId]),
        });
        // El id de este proyecto ya estaba mapeado a la carpeta vieja ("grabado") desde un sync previo.
        index.SaveIdMap(new Dictionary<string, string> { [LocalScanner.ProjectPathKey("grabado")] = projectId });

        // El remoto reporta el MISMO id con un nombre NUEVO ("grabado" -> "reunión semanal").
        var pullJson = $$"""
            {"serverTime":"2026-07-06T01:00:00Z",
             "projects":[{"id":"{{projectId}}","name":"reunión semanal","description":"","updated_at":"2026-07-06T01:00:00Z"}],
             "transcriptions":[]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // Una sola carpeta: la vieja ("grabado") ya no existe.
        Assert.False(Directory.Exists(Path.Combine(ws.AudiosPath, "grabado")));
        var renamed = ws.ListProjects().Single(p => !p.IsGeneral);
        Assert.Equal("reunión semanal", renamed.Name);

        // El color se preservó (venía de la carpeta vieja, el server nunca lo conoció).
        Assert.Equal("indigo", renamed.Color);

        // El audio que tenía la carpeta vieja se movió con ella (no quedó huérfano).
        Assert.True(File.Exists(Path.Combine(renamed.FolderPath, "reunion.mp3")));

        // El idOverrides ya no tiene la clave vieja.
        var idMap = index.LoadIdMap();
        Assert.False(idMap.ContainsKey(LocalScanner.ProjectPathKey("grabado")));
        Assert.True(idMap.ContainsKey(LocalScanner.ProjectPathKey("reunión semanal")));
    }

    // ---- Fix 2026-07-10 (MEDIUM): un audio no-subible ya no traba todo el sync ------------------

    [Fact]
    public async Task RunAsync_UnUploadDeAudioFalla_NoAbortaElCicloYLaBaselineAvanzaParaLosExitosos()
    {
        // Antes: una excepción de UploadAudioAsync (SyncApiException, p.ej. 413/415/500 persistente
        // del backend) abortaba el foreach ENTERO -- el resto del batch nunca se pusheaba y
        // SaveBaseline/SaveIdMap no corrían, así que NADA converge nunca (ni siquiera cambios sin
        // relación con el ítem fallido). El fix atrapa la excepción por acción: el resto del ciclo
        // sigue, y el ítem fallido queda afuera de la baseline nueva para reintentarse solo.
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");

        // (a) transcripción CON texto local -> va al batch de push, debería llegar OK.
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes-1");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "texto ok");

        // (b) audio nuevo SIN transcripción local -> dispara UploadAudioAsync (subida inmediata,
        // fuera del batch), que en este test va a fallar con 500.
        File.WriteAllText(Path.Combine(project.FolderPath, "falla.mp3"), "audio-bytes-2");

        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root);
        var projectId = snapshot.Projects.Values.Single().Id;
        var okId = snapshot.Transcriptions.Values.Single(t => t.AudioFileName == "reunion.mp3").Id;
        var failId = snapshot.Transcriptions.Values.Single(t => t.AudioFileName == "falla.mp3").Id;

        var apiHandler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var uploadHandler = new FakeHandler(req =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("upload failed") });

        var index = new SyncIndex(_dbPath);
        // ADR-06: sin esto, el scan interno de RunAsync acuñaría ids random NUEVOS -- distintos de
        // projectId/okId/failId precomputados arriba -- y las asserts de más abajo (que buscan esos
        // ids EXACTOS en la baseline nueva) no calzarían con nada.
        index.SaveIdMap(snapshot.MintedIds);
        var engine = BuildEngine(_root, _dbPath, apiHandler, uploadHandler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // El push del batch (proyecto + transcripción con texto) SÍ llegó, pese al fallo del upload.
        var pushBody = apiHandler.Bodies[apiHandler.Requests.FindIndex(r => r.Method == HttpMethod.Post)];
        Assert.Contains("texto ok", pushBody);

        // La baseline avanzó para los exitosos (proyecto y transcripción con texto)...
        var newBaseline = index.LoadBaseline();
        Assert.True(newBaseline.ContainsKey(projectId));
        Assert.True(newBaseline.ContainsKey(okId));

        // ...pero NO para el ítem que falló: sigue afuera, se reintenta el próximo ciclo.
        Assert.False(newBaseline.ContainsKey(failId));

        // Diagnóstico deja rastro del fallo.
        Assert.Contains(result.Diagnostics!, d => d.Contains(failId));
    }

    // ---- Bug real reportado 2026-07-21: no respetar el motor elegido (Local vs Groq) ----------
    // ExecutePushTranscriptionUpsertAsync, para un audio SIN transcripción local, subía SIEMPRE el
    // audio para que Groq lo transcriba server-side -- sin importar que el usuario tuviera elegido
    // el motor Local (+ diarización). El usuario arrastraba audios queriendo transcribirlos local
    // con diarización, y el auto-sync (cada 60s) se los transcribía con Groq (sin hablantes) antes
    // de que pudiera. Fix: nuevo parámetro autoUploadUntranscribed (default true, motor Groq) --
    // en false (motor Local), el audio sin transcripción local NO se sube: se espera a que el
    // usuario lo transcriba local.

    [Fact]
    public async Task RunAsync_AutoUploadUntranscribedFalse_NoSubeAudioSinTranscripcionLocal()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "nueva.mp3"), "audio-bytes");

        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root);
        var transcriptionId = snapshot.Transcriptions.Values.Single().Id;

        var apiHandler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var uploadHandler = new FakeHandler(req => Json("""{"ok":true}"""));

        var index = new SyncIndex(_dbPath);
        // ADR-06: fija el id de "nueva.mp3" acuñado arriba para que el scan interno de RunAsync
        // reutilice el MISMO id (si no, transcriptionId no calzaría con nada de la baseline nueva).
        index.SaveIdMap(snapshot.MintedIds);
        var engine = BuildEngine(_root, _dbPath, apiHandler, uploadHandler);

        var result = await engine.RunAsync("token-123", autoUploadUntranscribed: false);

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // Nunca se intentó subir el audio (ni prepare/put/transcribe ni el fallback crudo).
        Assert.Empty(uploadHandler.Requests);

        // La baseline avanza igual para este ítem (return sin excepción, ver
        // ExecutePushTranscriptionUpsertAsync/BuildBaselineEntry): NO se re-propone en loop cada
        // ciclo. Cuando el usuario transcriba local, el hash local cambia (texto + mtime, ver
        // LocalScanner) y el próximo ciclo SÍ sube la transcripción (con texto).
        var newBaseline = index.LoadBaseline();
        Assert.True(newBaseline.ContainsKey(transcriptionId));
    }

    [Fact]
    public async Task RunAsync_TranscripcionLocalDespuesDeCicloSalteadoPorMotorLocal_SePusheaEnElSiguienteCiclo()
    {
        // Reproduce el bug real reportado (2026-07-21, sync serio): con motor Local
        // (autoUploadUntranscribed:false), un audio SIN transcripción local se "saltea" en
        // ExecutePushTranscriptionUpsertAsync (return sin excepción) y la baseline se ancla igual
        // (local=hash-sin-texto, remote=''). Cuando el usuario transcribe local DESPUÉS, el
        // próximo ciclo DEBE detectar el cambio local (el hash ahora incluye el texto) y pushear
        // la transcripción -- si esto no pasa, queda huérfana en el desktop para siempre (nunca
        // llega al servidor, aunque HasLocalTranscript ya sea true).
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "nueva.mp3"), "audio-bytes");

        var apiHandler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, apiHandler);

        // Ciclo 1: motor Local, todavía sin transcripción local -> se saltea el upload (bugfix
        // 2026-07-21), la baseline se ancla igual. (El proyecto "Trabajo" en sí SÍ genera un POST
        // de push -- es nuevo -- pero ese batch no debe incluir la transcripción sin texto.)
        var first = await engine.RunAsync("token-123", autoUploadUntranscribed: false);
        Assert.Equal(SyncOutcome.Completed, first.Outcome);
        var firstPushIndex = apiHandler.Requests.FindIndex(r => r.Method == HttpMethod.Post);
        if (firstPushIndex >= 0)
            Assert.DoesNotContain("nueva.mp3", apiHandler.Bodies[firstPushIndex]);

        // El usuario transcribe local (36 min después, en el escenario real reportado).
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("nueva.mp3", "Trabajo"), "transcripcion local completa");

        // Ciclo 2: sigue en motor Local -- ahora SÍ hay texto local, así que debe pushear el TEXTO
        // (no un audio) al servidor. Se marca el corte de requests ANTES de este ciclo para no
        // confundir el push del ciclo 1 (proyecto nuevo) con el de este ciclo.
        var requestsBeforeCycle2 = apiHandler.Requests.Count;
        var second = await engine.RunAsync("token-123", autoUploadUntranscribed: false);

        Assert.Equal(SyncOutcome.Completed, second.Outcome);
        var pushRequestIndex = apiHandler.Requests.FindIndex(requestsBeforeCycle2, r => r.Method == HttpMethod.Post);
        Assert.True(pushRequestIndex >= 0, "Se esperaba un POST de push con la transcripción, pero no se mandó ninguno.");
        Assert.Contains("transcripcion local completa", apiHandler.Bodies[pushRequestIndex]);
    }

    // ---- Causa raíz real del bug de sync (2026-07-21): un ítem rechazado por el servidor queda
    // "falsamente sincronizado" -----------------------------------------------------------------
    // RunAsync construye newBaseline (BuildBaselineEntry, ANCLANDO el ítem como "sincronizado")
    // ANTES de conocer la respuesta del push (_api.PushAsync se llama DESPUÉS). El único mecanismo
    // que revierte algo tras conocer la respuesta es ResolveCascadeDeleteRejections, y SOLO
    // reacciona al patrón exacto de "borrado en cascada de proyecto" -- cualquier OTRO error en
    // errors[] (transcripción rechazada, project_id inexistente, fila que el backend simplemente
    // no encontró para actualizar, etc.) queda sin manejar: newBaseline YA tiene el ítem anclado
    // como éxito y nada lo revierte. Resultado: el ítem se guarda como "sincronizado" en la
    // baseline aunque el servidor lo haya rechazado -- exactamente la firma del bug real
    // reportado (LastLocalHash seteado, LastRemoteHash='' para siempre, sin reintentar nunca más).

    [Fact]
    public async Task RunAsync_PushDeTranscripcionRechazadoPorElServidor_NoAnclaLaBaselineComoSincronizada()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "nueva.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("nueva.mp3", "Trabajo"), "transcripcion local completa");

        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root);
        var transcriptionId = snapshot.Transcriptions.Values.Single().Id;

        // El backend responde 200 OK (como documenta PushResponse: SIEMPRE 200, "ok" es solo
        // errors.length === 0) pero con un error NO relacionado al patrón de borrado en cascada --
        // p.ej. el caso real confirmado en el backend web (api/sync/push/route.ts): un UPDATE sobre
        // un id que todavía no existe server-side no afecta ninguna fila, pero acá simulamos el
        // caso en que el backend SÍ reporta el rechazo explícitamente en errors[].
        var pushErrorJson = $$"""
            {"serverTime":"2026-07-21T00:00:00Z","ok":false,"errors":["Transcripción {{transcriptionId}}: no se pudo actualizar (no existe)."]}
            """;
        var apiHandler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json(pushErrorJson));
        var engine = BuildEngine(_root, _dbPath, apiHandler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // El push SÍ se intentó (el bucket incluía la transcripción)...
        var pushRequestIndex = apiHandler.Requests.FindIndex(r => r.Method == HttpMethod.Post);
        Assert.True(pushRequestIndex >= 0);
        Assert.Contains("transcripcion local completa", apiHandler.Bodies[pushRequestIndex]);

        // ...pero el servidor lo rechazó -- la baseline NO debe anclar este id como sincronizado.
        // Si queda anclada (bug real), el próximo ciclo nunca lo vuelve a intentar: la transcripción
        // queda invisible en el servidor para siempre pese a existir localmente con texto.
        var index = new SyncIndex(_dbPath);
        var newBaseline = index.LoadBaseline();
        Assert.False(
            newBaseline.ContainsKey(transcriptionId),
            "La transcripción quedó anclada como sincronizada pese a que el servidor rechazó el push -- se va a perder para siempre.");
    }

    [Fact]
    public async Task RunAsync_AutoUploadUntranscribedTrue_SubeAudioSinTranscripcionLocal()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "nueva.mp3"), "audio-bytes");

        var apiHandler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var uploadHandler = new FakeHandler(req => Json("""{"ok":true}"""));

        var engine = BuildEngine(_root, _dbPath, apiHandler, uploadHandler);

        // Comportamiento de siempre (motor Groq, autoUploadUntranscribed=true -- default):
        // el audio sin transcripción local SÍ se sube para que el backend lo transcriba.
        var result = await engine.RunAsync("token-123", autoUploadUntranscribed: true);

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Contains(uploadHandler.Requests, r => r.RequestUri!.AbsolutePath.Contains("/api/transcribe"));
    }

    // ---- Bug #1: un borrado desde el desktop no se propagaba a la nube -----------------------
    // MergeWithLocalTombstones ahora SÍ puede inyectar Deleted=true, pero SOLO para un id con un
    // tombstone local EXPLÍCITO (ver SyncIndex.AddLocalTombstone) -- nunca por la sola ausencia
    // del scan. Ver también RunAsync_AusenciaLocal_NoGeneraPushDelete más arriba, que cubre el
    // mismo invariante de seguridad sin ningún tombstone de por medio.

    [Fact]
    public async Task RunAsync_ItemAusenteConTombstoneParaOtroId_NoGeneraPushDeleteParaElAusente()
    {
        // INVARIANTE DE SEGURIDAD: un tombstone registrado para un id que NO es el que desapareció
        // no debe "contaminar" el item realmente ausente -- confirma que la inyección es por id,
        // no un interruptor global que reabra el bug de vaciado de cuenta.
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "nota.mp3"), "audio-bytes");
        File.WriteAllText(ws.TranscriptPathFor("nota.mp3"), "texto original");

        var scanner = new LocalScanner();
        var initialScan = scanner.Scan(_root);
        var transcriptionId = initialScan.First(kv => kv.Value.Kind == SyncItemKind.Transcription).Key;

        var index = new SyncIndex(_dbPath);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [transcriptionId] = AsBaseline(initialScan[transcriptionId]),
        });
        // Tombstone para un id CUALQUIERA, no relacionado con "nota.mp3".
        index.AddLocalTombstone("id-no-relacionado", SyncItemKind.Transcription);

        // Mismo gap de siempre: el audio desaparece de disco sin que el usuario lo haya borrado.
        File.Delete(Path.Combine(ws.AudiosPath, "nota.mp3"));

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Actions, a => a.Type == SyncActionType.PushDelete);
        Assert.False(index.LoadBaseline()[transcriptionId].Deleted);
    }

    [Fact]
    public async Task RunAsync_ConTombstoneExplicitoParaItemVivo_GeneraPushDeleteYLimpiaElTombstone()
    {
        // El caso que el bug #1 necesitaba: el usuario borra desde el desktop (Workspace.DeleteAudio
        // ya movió el archivo a .papelera/ -- acá se simula sacándolo de disco) Y se registró el
        // tombstone explícito (lo que va a hacer SyncCoordinator.MarkAudioDeletedForSync). Debe
        // viajar como PushDelete, la baseline debe quedar Deleted=true, y el tombstone -- ya
        // resuelto -- debe limpiarse para no reintentarlo de nuevo el próximo ciclo.
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "nota.mp3"), "audio-bytes");
        File.WriteAllText(ws.TranscriptPathFor("nota.mp3"), "texto original");
        // Otros audios sin cambios, solo para diluir el % de borrados de este ciclo por debajo
        // del freno anti-borrado-masivo (SyncEngine.MassDeletionThreshold) -- no es lo que este
        // test ejercita, ver RunAsync_BorradoMasivo_* más arriba para ESE comportamiento.
        File.WriteAllText(Path.Combine(ws.AudiosPath, "otra1.mp3"), "audio-bytes-1");
        File.WriteAllText(ws.TranscriptPathFor("otra1.mp3"), "sin cambios 1");
        File.WriteAllText(Path.Combine(ws.AudiosPath, "otra2.mp3"), "audio-bytes-2");
        File.WriteAllText(ws.TranscriptPathFor("otra2.mp3"), "sin cambios 2");

        var scanner = new LocalScanner();
        var initialSnapshot = scanner.ScanDetailed(_root);
        var initialScan = initialSnapshot.Items;
        // ADR-06: ResolveTranscriptionId ya no puede "adivinar" el id sin idMap (Task 1.3/1.4) --
        // se toma el id que este scan realmente acuñó para "nota.mp3" (General, sin proyecto).
        var transcriptionId = initialSnapshot.Transcriptions.Values.Single(t => t.AudioFileName == "nota.mp3").Id;
        var baselineEntries = initialScan.ToDictionary(kv => kv.Key, kv => AsBaseline(kv.Value));

        var index = new SyncIndex(_dbPath);
        // Persiste los ids acuñados en este scan para que el scan interno de RunAsync los reutilice
        // vía override en vez de acuñar otros random (si no, ni la baseline ni el tombstone calzan).
        index.SaveIdMap(initialSnapshot.MintedIds);
        index.SaveBaseline(baselineEntries); // todos vivos, Deleted=false
        index.AddLocalTombstone(transcriptionId, SyncItemKind.Transcription);

        // El borrado real ya movió el audio y el .txt a .papelera/ (ver Workspace.DeleteAudio) --
        // acá alcanza con que ya no estén en su ubicación original.
        File.Delete(Path.Combine(ws.AudiosPath, "nota.mp3"));
        File.Delete(ws.TranscriptPathFor("nota.mp3"));

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        var deleteAction = Assert.Single(result.Actions, a => a.Type == SyncActionType.PushDelete);
        Assert.Equal(transcriptionId, deleteAction.Id);

        // Se avisó al servidor.
        var pushBody = handler.Bodies[handler.Requests.FindIndex(r => r.Method == HttpMethod.Post)];
        Assert.Contains(transcriptionId, pushBody);

        // La baseline quedó marcada como borrada.
        Assert.True(index.LoadBaseline()[transcriptionId].Deleted);

        // El tombstone, ya resuelto, se limpió -- no debe reintentarse en el próximo ciclo.
        Assert.Empty(index.LoadLocalTombstones());
    }

    [Fact]
    public async Task RunAsync_TombstoneParaIdQueNoEstaEnBaseline_NoGeneraAccionYSeLimpiaElTombstoneStale()
    {
        // Un tombstone para un id que nunca llegó a sincronizarse (o ya no existe en la baseline)
        // no tiene nada que borrar en el servidor -- no debe generar ninguna acción, y el tombstone
        // stale se limpia para no acumularse para siempre.
        var index = new SyncIndex(_dbPath);
        index.AddLocalTombstone("id-nunca-sincronizado", SyncItemKind.Transcription);

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Actions, a => a.Id == "id-nunca-sincronizado");
        Assert.Empty(index.LoadLocalTombstones());
    }

    // ---- Phase 4 (ADR-06 §7, Riesgo #1 / ADR-07e): reconciliación incondicional --------------
    // Hoy `newIdOverrides` solo se toca DENTRO de los `case SyncActionType.PullUpsert`
    // (SyncEngine.cs, dentro del switch de ejecución de acciones) -- o sea, SOLO para ítems que
    // generaron una acción este ciclo. En régimen, la mayoría de los ítems del pull NO generan
    // acción (mismo hash que la baseline, "sin cambios") -- sin un backfill incondicional, esos
    // ítems nunca registran su mapeo PathKey->id, y el próximo LocalScanner.ScanDetailed les
    // acuña un id NUEVO -- duplicación masiva del workspace entero (Riesgo #1 del design).

    [Fact]
    public async Task RunAsync_ItemDelPullSinAccion_IgualRegistraSuPathKeyEnSyncIdMap()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "hola");

        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root);
        var projectId = snapshot.Projects.Values.Single().Id;
        var transcriptionId = snapshot.Transcriptions.Values.Single().Id;

        var pullJson = $$"""
            {"serverTime":"2026-07-28T00:00:00Z",
             "projects":[{"id":"{{projectId}}","name":"Trabajo","updated_at":"2026-07-06T00:00:00Z"}],
             "transcriptions":[{"id":"{{transcriptionId}}","project_id":"{{projectId}}","audio_name":"reunion.mp3","text":"hola","updated_at":"2026-07-06T00:00:00Z"}]}
            """;
        var parsedPull = System.Text.Json.JsonSerializer.Deserialize<PullResponse>(pullJson)!;
        var remoteMapped = new RemoteMapper().Map(parsedPull);

        var index = new SyncIndex(_dbPath);
        // Baseline YA sincronizada (simula un ciclo previo) -- el idMap deliberadamente vacío: el
        // mismo hueco que describe Riesgo #1 (p.ej. un upgrade desde antes de que existiera el
        // acuñado persistente, o un SyncIdMap.db perdido/corrupto).
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(snapshot.Items[projectId], remoteHash: remoteMapped[projectId].ContentHash),
            [transcriptionId] = AsBaseline(snapshot.Items[transcriptionId], remoteHash: remoteMapped[transcriptionId].ContentHash),
        });

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Empty(result.Actions); // mismo contenido en base/local/remoto -- sin acción.

        var idMap = index.LoadIdMap();
        Assert.Equal(projectId, idMap[LocalScanner.ProjectPathKey("Trabajo")]);
        Assert.Equal(transcriptionId, idMap[LocalScanner.TranscriptionPathKey("Trabajo", "reunion.mp3")]);
    }

    [Fact]
    public async Task RunAsync_ItemDelPullSinAccion_ActualizaLastRemoteVersionEnLaBaseline()
    {
        // Test de RED explícito pedido por el design (ADR-07e): un ciclo sin acciones sobre un
        // ítem cuyo version remoto avanzó igual deja LastRemoteVersion actualizado en la baseline
        // -- si no, el próximo push legítimo de este ítem manda un base_version viejo y el
        // servidor lo rechaza por "conflict" para siempre (sync trabado).
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");

        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root);
        var projectId = snapshot.Projects.Values.Single().Id;

        var pullJson = $$"""
            {"serverTime":"2026-07-28T00:00:00Z",
             "projects":[{"id":"{{projectId}}","name":"Trabajo","updated_at":"2026-07-06T00:00:00Z","version":7}],
             "transcriptions":[]}
            """;
        var parsedPull = System.Text.Json.JsonSerializer.Deserialize<PullResponse>(pullJson)!;
        var remoteMapped = new RemoteMapper().Map(parsedPull);

        var index = new SyncIndex(_dbPath);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(snapshot.Items[projectId], remoteHash: remoteMapped[projectId].ContentHash)
                with { LastRemoteVersion = 3 },
        });
        index.SaveIdMap(snapshot.MintedIds);

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Empty(result.Actions);

        Assert.Equal(7, index.LoadBaseline()[projectId].LastRemoteVersion);
    }

    [Fact]
    public async Task RunAsync_PathKeyReidentificadoConOtroId_DisparaRekeyBaselineSinDejarHuerfana()
    {
        var ws = Workspace.OpenOrCreate(_root);
        ws.CreateProject("Trabajo");

        var scanner = new LocalScanner();
        var localState = scanner.Scan(_root).Values.Single();

        const string oldId = "old-11111111-1111-1111-1111-111111111111";
        const string newId = "new-22222222-2222-2222-2222-222222222222";

        var index = new SyncIndex(_dbPath);
        index.SaveIdMap(new Dictionary<string, string> { [LocalScanner.ProjectPathKey("Trabajo")] = oldId });
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [oldId] = new SyncBaselineItem(oldId, SyncItemKind.Project, localState.ContentHash, localState.ContentHash, localState.UpdatedAt),
        });

        // El pull re-identifica la MISMA carpeta ("Trabajo") con un id CANÓNICO distinto -- el
        // caso de migración de ADR-06 §7.
        var pullJson = $$"""
            {"serverTime":"2026-07-28T00:00:00Z",
             "projects":[{"id":"{{newId}}","name":"Trabajo","updated_at":"2026-07-06T00:00:00Z","version":1}],
             "transcriptions":[]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        var newBaseline = index.LoadBaseline();
        Assert.False(newBaseline.ContainsKey(oldId), "la entrada vieja de baseline debe migrarse (rekey), no quedar huérfana");

        var idMap = index.LoadIdMap();
        Assert.Equal(newId, idMap[LocalScanner.ProjectPathKey("Trabajo")]);
    }

    [Fact]
    public async Task RunAsync_ItemDelPullSinAccionConNombreQueRequiereSaneado_ElPathKeyDeLaReconciliacionCoincideConElDelScanLocal()
    {
        // El servidor manda "name" tal cual lo tipeó el usuario (puede tener caracteres inválidos
        // para Windows); el PathKey local SIEMPRE se deriva del nombre de CARPETA, que es el
        // nombre YA saneado (Workspace.Sanitize). Si la reconciliación saneara distinto de como
        // sanea LocalScanner/Workspace.CreateProject, el PathKey no matchea y el próximo scan
        // acuña un id nuevo para la MISMA carpeta -- mismo criterio que protege el caso huérfano-
        // por-stem (LocalScanner.cs:806-807), que no puede regresionar.
        const string rawName = "Reunión: Año 2026"; // ':' es inválido en Windows.
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject(rawName); // ya crea la carpeta CON el nombre saneado.

        var scanner = new LocalScanner();
        var localState = scanner.Scan(_root).Values.Single();

        var pullJson = $$"""
            {"serverTime":"2026-07-28T00:00:00Z",
             "projects":[{"id":"remote-reunion","name":"{{rawName}}","updated_at":"2026-07-06T00:00:00Z","version":1}],
             "transcriptions":[]}
            """;
        var parsedPull = System.Text.Json.JsonSerializer.Deserialize<PullResponse>(pullJson)!;
        var remoteHash = new RemoteMapper().Map(parsedPull)["remote-reunion"].ContentHash;

        var index = new SyncIndex(_dbPath);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            ["remote-reunion"] = new SyncBaselineItem(
                "remote-reunion", SyncItemKind.Project, localState.ContentHash, remoteHash, localState.UpdatedAt),
        });
        // idMap deliberadamente vacío -- mismo hueco de Riesgo #1.

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Empty(result.Actions);

        var expectedPathKey = LocalScanner.ProjectPathKey(project.Name); // project.Name = carpeta ya saneada.
        var idMap = index.LoadIdMap();
        Assert.Equal("remote-reunion", idMap[expectedPathKey]);
    }

    // ---- Task 4.5: pull completo único (since=null forzado) tras el upgrade -------------------

    [Fact]
    public async Task RunAsync_PrimerCicloSinFullPullDone_FuerzaSinceNullAunqueElCallerPaseUnoExplicito()
    {
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var explicitSince = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = await engine.RunAsync("token-123", since: explicitSince);

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        var pullRequest = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        Assert.DoesNotContain("since=", pullRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task RunAsync_TrasCompletarElPrimerCiclo_LosSiguientesRespetanElSinceExplicito()
    {
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(EmptyPull) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        await engine.RunAsync("token-123"); // primer ciclo: marca SyncMeta.FullPullDone.

        var explicitSince = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await engine.RunAsync("token-123", since: explicitSince);

        var secondPullRequest = handler.Requests.Where(r => r.Method == HttpMethod.Get).Skip(1).Single();
        Assert.Contains("since=", secondPullRequest.RequestUri!.ToString());
    }

    // ---- Gap explícito dejado por Phase 1-3: RunAsync_PullUpsertDeProyectoYaLocalConColor_
    // PreservaElColorEnDisco (más arriba) pasaba SOLO porque ExecutePullProjectUpsert resuelve por
    // NOMBRE de carpeta, no por id -- ese test nunca guarda idMap antes del ciclo, así que el scan
    // INTERNO de RunAsync (idOverrides vacío en ese momento) mintea un id NUEVO para "grabado" y lo
    // pushea como proyecto nuevo -- un duplicado FANTASMA en el servidor -- mientras el PullUpsert
    // del id canónico de la baseline resuelve por nombre sobre la MISMA carpeta física, dejando el
    // test viejo en verde sin que nadie note el duplicado (no inspecciona los requests de push).
    // La reconciliación incondicional de Phase 4 corre ANTES del scan local, así que el scan nunca
    // llega a acuñar ese id fantasma.

    [Fact]
    public async Task RunAsync_PullUpsertDeProyectoYaLocalSinIdMapPrevio_NoGeneraUnPushUpsertFantasmaDelIdRecienAcunado()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("grabado");
        project.Color = "indigo";
        ws.SaveProjectMeta(project);

        var scanner = new LocalScanner();
        var initialScan = scanner.Scan(_root);
        var projectId = initialScan.First(kv => kv.Value.Kind == SyncItemKind.Project).Key;

        var index = new SyncIndex(_dbPath);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(initialScan[projectId]),
        });
        // Deliberadamente SIN SaveIdMap -- el mismo hueco del test original.

        var pullJson = $$"""
            {"serverTime":"2026-07-06T01:00:00Z",
             "projects":[{"id":"{{projectId}}","name":"grabado","description":"editado desde la web","updated_at":"2026-07-06T01:00:00Z","version":2}],
             "transcriptions":[]}
            """;
        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json("""{"ok":true}"""));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // El único cambio real es un PullUpsert legítimo del id canónico -- ningún id fantasma
        // recién acuñado debería generar un PushUpsert de proyecto, ni un POST al backend.
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(result.Actions, a => a.Kind == SyncItemKind.Project && a.Type == SyncActionType.PushUpsert);
        Assert.Contains(result.Actions, a => a.Id == projectId && a.Type == SyncActionType.PullUpsert);

        // Una sola carpeta física en disco, sin duplicados.
        Assert.Single(ws.ListProjects(), p => !p.IsGeneral);
    }

    // ---- Task 5.4 (ADR-07d): wiring del ConflictResolver en ReconcilePushResponse -------------

    [Fact]
    public async Task RunAsync_PushRechazadoPorConflictDeVersion_PreservaLocalComoHermanoYAdoptaLaCopiaRemota()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "texto viejo sincronizado");

        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root);
        var projectId = snapshot.Projects.Values.Single().Id;
        var transcriptionId = snapshot.Transcriptions.Values.Single().Id;

        var index = new SyncIndex(_dbPath);
        index.SaveIdMap(snapshot.MintedIds);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(snapshot.Items[projectId]),
            [transcriptionId] = AsBaseline(snapshot.Items[transcriptionId]) with { LastRemoteVersion = 4 },
        });

        // El usuario edita localmente -> dispara un PushUpsert de transcripción con base_version=4.
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "edición local del usuario");

        // El pull de ESTE MISMO ciclo ya trae la copia remota ganadora (alguien más la editó
        // primero, version subió a 9).
        var pullJson = $$"""
            {"serverTime":"2026-07-28T00:00:00Z",
             "projects":[],
             "transcriptions":[{"id":"{{transcriptionId}}","project_id":"{{projectId}}","audio_name":"reunion.mp3","text":"edición ganadora del servidor","updated_at":"2026-07-28T00:00:00Z","version":9}]}
            """;

        // El push responde "conflict" para esta transcripción (base_version=4 quedó vieja).
        var pushResponseJson = $$"""
            {"serverTime":"2026-07-28T00:00:00Z","ok":true,"errors":[],
             "results":[{"id":"{{transcriptionId}}","kind":"transcription","status":"conflict","version":9}]}
            """;

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json(pushResponseJson));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // El push se mandó con el base_version PRE-ciclo (4) -- no el que este mismo pull acaba de
        // revelar (9) -- si no, el servidor nunca vería la staleness real.
        var pushBody = handler.Bodies[handler.Requests.FindIndex(r => r.Method == HttpMethod.Post)];
        Assert.Contains("\"base_version\":4", pushBody);

        // La ruta canónica queda con la copia REMOTA (el servidor ganó).
        Assert.Equal("edición ganadora del servidor", File.ReadAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo")));

        // La edición LOCAL no se perdió: quedó preservada en el hermano .conflicto-*.
        var siblingFiles = Directory.GetFiles(Path.Combine(ws.TranscriptsPath, "Trabajo"), "reunion.conflicto-*.txt");
        var sibling = Assert.Single(siblingFiles);
        Assert.Equal("edición local del usuario", File.ReadAllText(sibling));

        // La baseline adoptó la version del servidor -- el próximo push manda base_version=9, no 4.
        var newBaseline = index.LoadBaseline();
        Assert.Equal(9, newBaseline[transcriptionId].LastRemoteVersion);
    }

    // ---- Phase 7 (gap documentado en Phase 5.4/ADR-07e): wiring de status=="ok" en results[] --

    [Fact]
    public async Task RunAsync_PushExitoso_ActualizaLastRemoteVersionConLaVersionDevueltaPorElServidor()
    {
        // Gap explícito dejado por Phase 5: ResolveConflicts solo procesaba status=="conflict".
        // Un push exitoso (status:"ok") deja LastRemoteVersion en la baseline SIN refrescar
        // (BuildBaselineEntry preserva el valor viejo a propósito para un push, ver el comentario
        // largo en RunAsync) -- si nada más lo refresca, el PRÓXIMO push de este mismo ítem manda
        // un base_version viejo, y el servidor lo rechaza como "conflict" contra su propia
        // escritura recién aceptada -- el "sync trabado" que describe ADR-07e, ahora disparado por
        // el propio cliente en vez de por un pull incremental.
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "texto viejo sincronizado");

        var scanner = new LocalScanner();
        var snapshot = scanner.ScanDetailed(_root);
        var projectId = snapshot.Projects.Values.Single().Id;
        var transcriptionId = snapshot.Transcriptions.Values.Single().Id;

        var index = new SyncIndex(_dbPath);
        index.SaveIdMap(snapshot.MintedIds);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            [projectId] = AsBaseline(snapshot.Items[projectId]),
            [transcriptionId] = AsBaseline(snapshot.Items[transcriptionId]) with { LastRemoteVersion = 4 },
        });

        // El usuario edita localmente -> dispara un PushUpsert de transcripción con base_version=4.
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "edición local del usuario");

        var pullJson = $$"""
            {"serverTime":"2026-07-28T00:00:00Z","projects":[],"transcriptions":[]}
            """;

        // El push acepta el cambio: el trigger del server subió version de 4 a 5.
        var pushResponseJson = $$"""
            {"serverTime":"2026-07-28T00:00:00Z","ok":true,"errors":[],
             "results":[{"id":"{{transcriptionId}}","kind":"transcription","status":"ok","version":5}]}
            """;

        var handler = new FakeHandler(req => req.Method == HttpMethod.Get ? Json(pullJson) : Json(pushResponseJson));
        var engine = BuildEngine(_root, _dbPath, handler);

        var result = await engine.RunAsync("token-123");

        Assert.Equal(SyncOutcome.Completed, result.Outcome);

        // La baseline adoptó la version que el server efectivamente devolvió -- el próximo push
        // manda base_version=5, no el 4 que quedó anclado desde ANTES de este mismo push.
        var newBaseline = index.LoadBaseline();
        Assert.Equal(5, newBaseline[transcriptionId].LastRemoteVersion);
    }
}
