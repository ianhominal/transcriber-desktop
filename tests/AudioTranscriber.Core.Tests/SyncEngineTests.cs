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
        var initialScan = scanner.Scan(_root);
        var projectId = initialScan.First(kv => kv.Value.Kind == SyncItemKind.Project).Key;
        var transcriptionId = initialScan.First(kv => kv.Value.Kind == SyncItemKind.Transcription).Key;

        var index = new SyncIndex(_dbPath);
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
        var rescan = scanner.Scan(_root);
        Assert.Equal(rescan[transcriptionId].ContentHash, newBaseline[transcriptionId].LastLocalHash);
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
        var scan = scanner.Scan(_root);
        var projectId = scan.First(kv => kv.Value.Kind == SyncItemKind.Project).Key;
        var transcriptionId = scan.First(kv => kv.Value.Kind == SyncItemKind.Transcription).Key;

        var index = new SyncIndex(_dbPath);
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
        var initialScan = scanner.Scan(_root);
        var transcriptionId = LocalScanner.ResolveTranscriptionId(null, "nota.mp3", new Dictionary<string, string>());
        var baselineEntries = initialScan.ToDictionary(kv => kv.Key, kv => AsBaseline(kv.Value));

        var index = new SyncIndex(_dbPath);
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
}
