using AudioTranscriber.Core.Sync;
using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.Tests;

public class LocalScannerTests : IDisposable
{
    private readonly string _root;
    private readonly LocalScanner _scanner = new();

    public LocalScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "at_tests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Workspace SeedWorkspace()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        File.WriteAllText(Path.Combine(project.FolderPath, "reunion.mp3"), "audio-bytes");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("reunion.mp3", "Trabajo"), "hola mundo");
        return ws;
    }

    [Fact]
    public void Scan_ProyectoConTranscripcion_GeneraProyectoYTranscripcion()
    {
        SeedWorkspace();

        var snapshot = _scanner.ScanDetailed(_root);

        Assert.Single(snapshot.Projects);
        Assert.Single(snapshot.Transcriptions);

        var project = snapshot.Projects.Values.Single();
        Assert.Equal("Trabajo", project.Name);
        Assert.Equal(SyncItemKind.Project, project.State.Kind);

        var transcription = snapshot.Transcriptions.Values.Single();
        Assert.Equal("reunion.mp3", transcription.AudioFileName);
        Assert.Equal(SyncItemKind.Transcription, transcription.State.Kind);
        Assert.True(transcription.HasLocalTranscript);
        Assert.Equal(project.Id, transcription.ProjectId);
    }

    [Fact]
    public void Scan_AudioSinTranscript_SeIncluyeComoTranscripcionPendiente()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Personal");
        File.WriteAllText(Path.Combine(project.FolderPath, "nota.m4a"), "audio-bytes");

        var snapshot = _scanner.ScanDetailed(_root);

        var transcription = snapshot.Transcriptions.Values.Single();
        Assert.False(transcription.HasLocalTranscript);
        Assert.Equal(string.Empty, transcription.Text);
    }

    [Fact]
    public void Scan_AudioSuelto_ProyectoGeneral_TranscripcionSinProjectId()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "suelto.wav"), "audio-bytes");
        File.WriteAllText(ws.TranscriptPathFor("suelto.wav"), "texto suelto");

        var snapshot = _scanner.ScanDetailed(_root);

        // "General" no genera un SyncItemState de proyecto (no viaja como project real).
        Assert.Empty(snapshot.Projects);
        var transcription = snapshot.Transcriptions.Values.Single();
        Assert.Null(transcription.ProjectId);
    }

    [Fact]
    public void Scan_CarpetaSinCambios_ProduceMismoContentHash()
    {
        // ADR-06: por default el id ya NO es estable entre scans (se acuña random sin override,
        // ver Scan_MismoPathKeySinOverride_GeneraUnUuidDistintoEnCadaScan) -- eso es intencional y
        // no lo que este test cubre. Acá interesa una propiedad DISTINTA (el ContentHash de un item
        // sin cambios es reproducible), así que se fija el id vía mintId determinístico SOLO para
        // poder comparar por clave entre los dos scans.
        SeedWorkspace();
        Func<string, string> mintId = pathKey => pathKey;

        var first = _scanner.ScanDetailed(_root, mintId: mintId).Items;
        var second = _scanner.ScanDetailed(_root, mintId: mintId).Items;

        Assert.Equal(first.Keys.OrderBy(k => k), second.Keys.OrderBy(k => k));
        foreach (var id in first.Keys)
            Assert.Equal(first[id].ContentHash, second[id].ContentHash);
    }

    [Fact]
    public void Scan_ConIdOverride_UsaElIdProvisto_EnVezDelHash()
    {
        SeedWorkspace();
        var pathKey = LocalScanner.ProjectPathKey("Trabajo");
        var overrides = new Dictionary<string, string> { [pathKey] = "remote-project-id" };

        var snapshot = _scanner.ScanDetailed(_root, overrides);

        Assert.True(snapshot.Projects.ContainsKey("remote-project-id"));
    }

    [Fact]
    public void Scan_ProyectoNuevoSinOverride_GeneraIdConFormatoUuidValido()
    {
        // Regresión: el id local se mandaba al backend como hex crudo de 64 caracteres
        // (ContentHasher.Hash), que la columna `projects.id` (uuid) de Postgres rechazaba con un
        // 500 al hacer push de un proyecto nuevo creado localmente.
        SeedWorkspace();

        var snapshot = _scanner.ScanDetailed(_root);
        var project = snapshot.Projects.Values.Single();

        Assert.True(Guid.TryParse(project.Id, out _), $"'{project.Id}' no es un UUID válido");
    }

    [Fact]
    public void Scan_TranscripcionNuevaSinOverride_GeneraIdConFormatoUuidValido()
    {
        SeedWorkspace();

        var snapshot = _scanner.ScanDetailed(_root);
        var transcription = snapshot.Transcriptions.Values.Single();

        Assert.True(Guid.TryParse(transcription.Id, out _), $"'{transcription.Id}' no es un UUID válido");
    }

    [Fact]
    public void Scan_MismoPathKeySinOverride_GeneraUnUuidDistintoEnCadaScan()
    {
        // ADR-06: HashId (determinístico) desaparece del camino de identidad -- la determinismo
        // ERA el bug (CRÍTICO-1: dos cuentas con el mismo nombre de proyecto colisionaban). El id
        // por default ahora es un UUIDv4 aleatorio, acuñado de nuevo en CADA scan que no tenga un
        // idOverride para ese PathKey. Por eso la estabilidad entre ciclos ya NO es responsabilidad
        // de LocalScanner: la persiste SyncEngine vía MintedIds -> SyncIndex.SaveIdMap (ver
        // SyncEngineTests, "el id acuñado sobrevive a un push que falla").
        SeedWorkspace();

        var first = _scanner.ScanDetailed(_root).Projects.Values.Single().Id;
        var second = _scanner.ScanDetailed(_root).Projects.Values.Single().Id;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Scan_ConMintIdInyectado_UsaEseDelegadoEnVezDeGenerarUnIdAleatorio()
    {
        // Task 1.1: el caller (SyncEngine) inyecta CÓMO acuñar un id nuevo -- ScanDetailed ya no
        // decide por su cuenta (ni HashId determinístico, ni Guid.NewGuid implícito e inyectable).
        SeedWorkspace();

        var snapshot = _scanner.ScanDetailed(_root, mintId: _ => "id-acunado-fijo");

        var project = snapshot.Projects.Values.Single();
        Assert.Equal("id-acunado-fijo", project.Id);
    }

    [Fact]
    public void Scan_SinOverride_RegistraElParPathKeyIdEnMintedIds()
    {
        // MintedIds es lo que SyncEngine mergea en su propio mapa de overrides inmediatamente
        // después del primer scan del ciclo (ver diseño ADR-06.2/3) para que el id acuñado
        // sobreviva al rescan y se persista aunque el push falle.
        SeedWorkspace();
        var projectPathKey = LocalScanner.ProjectPathKey("Trabajo");
        var transcriptionPathKey = LocalScanner.TranscriptionPathKey("Trabajo", "reunion.mp3");

        var snapshot = _scanner.ScanDetailed(_root);

        Assert.True(snapshot.MintedIds.ContainsKey(projectPathKey));
        Assert.Equal(snapshot.Projects.Values.Single().Id, snapshot.MintedIds[projectPathKey]);
        Assert.True(snapshot.MintedIds.ContainsKey(transcriptionPathKey));
        Assert.Equal(snapshot.Transcriptions.Values.Single().Id, snapshot.MintedIds[transcriptionPathKey]);
    }

    [Fact]
    public void Scan_ConIdOverride_NoRegistraEseItemEnMintedIds()
    {
        // Un item resuelto por override NO se "acuñó" en este scan -- no debe aparecer en
        // MintedIds (si no, SyncEngine lo re-guardaría como si fuera nuevo).
        SeedWorkspace();
        var pathKey = LocalScanner.ProjectPathKey("Trabajo");
        var overrides = new Dictionary<string, string> { [pathKey] = "remote-project-id" };

        var snapshot = _scanner.ScanDetailed(_root, overrides);

        Assert.False(snapshot.MintedIds.ContainsKey(pathKey));
    }

    // ---- Transcripciones SOLO TEXTO (bug: invisibles para siempre en desktop) ----------------

    [Fact]
    public void Scan_TranscripcionSoloTextoConOverride_SeIncluyeEnElSnapshotSinTirarExcepcion()
    {
        // Con override (lo deja SyncEngine al bajar una transcripción sin audio_url_signed, ver
        // ExecutePullTranscriptionUpsertAsync): se incluye en el snapshot de sync con normalidad.
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Trabajo");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Trabajo"));
        File.WriteAllText(ws.TranscriptPathFor("nota.mp3", "Trabajo"), "solo texto, sin audio");
        var pathKey = LocalScanner.TranscriptionPathKey("Trabajo", "nota");
        var overrides = new Dictionary<string, string> { [pathKey] = "remote-transcription-id" };

        var snapshot = _scanner.ScanDetailed(_root, overrides);

        var transcription = snapshot.Transcriptions.Values.Single();
        Assert.Equal("nota", transcription.AudioFileName);
        Assert.True(transcription.HasLocalTranscript);
        Assert.Equal("solo texto, sin audio", transcription.Text);
    }

    [Fact]
    public void Scan_TranscripcionSoloTextoSinOverride_SeExcluyeDelSnapshotDeSync()
    {
        // Sin override conocido, un .txt huérfano NO se incluye en el snapshot de sync (mismo
        // criterio de seguridad que MergeWithLocalTombstones: nunca sintetizar una identidad por
        // inferencia). Sigue siendo visible en la UI vía Workspace.ListProjects() directo -- eso
        // NO pasa por LocalScanner, ver MainViewModel.RefreshAudios.
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(ws.TranscriptPathFor("huerfano.mp3"), "sin identidad conocida");

        var snapshot = _scanner.ScanDetailed(_root);

        Assert.Empty(snapshot.Transcriptions);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void Scan_ConIdOverrideParaTranscripcionSoloTexto_UsaElIdProvisto()
    {
        // El pathKey de una transcripción solo-texto se arma con el STEM del .txt (sin
        // extensión) -- ver SyncEngine.ExecutePullTranscriptionUpsertAsync, que guarda el
        // idOverride con esta misma convención para que el próximo scan reconozca el mismo id
        // en vez de generar uno nuevo (y duplicarla en el próximo push).
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(ws.TranscriptPathFor("nota.mp3"), "solo texto");
        var pathKey = LocalScanner.TranscriptionPathKey(null, "nota");
        var overrides = new Dictionary<string, string> { [pathKey] = "remote-transcription-id" };

        var snapshot = _scanner.ScanDetailed(_root, overrides);

        Assert.True(snapshot.Transcriptions.ContainsKey("remote-transcription-id"));
    }

    // ---- ResolveTranscriptionId (bug #1: borrado local no se propagaba a la nube) -------------
    // SyncCoordinator.MarkAudioDeletedForSync necesita resolver el MISMO id que produce el scan
    // para un audio, sin tener que correr un ScanDetailed completo, para poder registrar el
    // tombstone de sync en el momento del borrado (ver Workspace.DeleteAudio).

    [Fact]
    public void ResolveTranscriptionId_SinOverride_DevuelveNull()
    {
        // Task 1.3/ADR-06: sin HashId no hay forma de "adivinar" el id de un ítem que nunca se
        // sincronizó -- inventar uno acá sintetizaría una identidad por inferencia (la misma
        // doctrina que ya protege MergeWithLocalTombstones). null = "no se sabe qué borrar": el
        // caller (SyncCoordinator.MarkAudioDeletedForSync) NO debe registrar tombstone.
        SeedWorkspace();

        var resolved = LocalScanner.ResolveTranscriptionId("Trabajo", "reunion.mp3", new Dictionary<string, string>());

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveTranscriptionId_ConOverride_UsaElIdDelMapa()
    {
        var pathKey = LocalScanner.TranscriptionPathKey("Trabajo", "reunion.mp3");
        var idMap = new Dictionary<string, string> { [pathKey] = "remote-id-1" };

        var resolved = LocalScanner.ResolveTranscriptionId("Trabajo", "reunion.mp3", idMap);

        Assert.Equal("remote-id-1", resolved);
    }

    [Fact]
    public void ResolveTranscriptionId_ProyectoGeneral_SinOverride_DevuelveNull()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "suelto.wav"), "audio-bytes");

        var resolved = LocalScanner.ResolveTranscriptionId(null, "suelto.wav", new Dictionary<string, string>());

        Assert.Null(resolved);
    }
}
