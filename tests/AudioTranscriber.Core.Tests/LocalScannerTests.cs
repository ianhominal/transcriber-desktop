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
        SeedWorkspace();

        var first = _scanner.Scan(_root);
        var second = _scanner.Scan(_root);

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
    public void Scan_MismoPathKey_GeneraSiempreElMismoUuid_SinNecesidadDePersistirNada()
    {
        // El id derivado del hash es puro y determinístico: no hace falta que el id-map de
        // SyncIndex lo persista para que sea estable entre ciclos (a diferencia de un
        // Guid.NewGuid() aleatorio, que sí lo necesitaría).
        SeedWorkspace();

        var first = _scanner.ScanDetailed(_root).Projects.Values.Single().Id;
        var second = _scanner.ScanDetailed(_root).Projects.Values.Single().Id;

        Assert.Equal(first, second);
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
    public void ResolveTranscriptionId_SinOverride_CoincideConElIdDelScanReal()
    {
        SeedWorkspace();

        var snapshot = _scanner.ScanDetailed(_root);
        var expected = snapshot.Transcriptions.Values.Single().Id;

        var resolved = LocalScanner.ResolveTranscriptionId("Trabajo", "reunion.mp3", new Dictionary<string, string>());

        Assert.Equal(expected, resolved);
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
    public void ResolveTranscriptionId_ProyectoGeneral_CoincideConElIdDelScanReal()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "suelto.wav"), "audio-bytes");

        var snapshot = _scanner.ScanDetailed(_root);
        var expected = snapshot.Transcriptions.Values.Single().Id;

        var resolved = LocalScanner.ResolveTranscriptionId(null, "suelto.wav", new Dictionary<string, string>());

        Assert.Equal(expected, resolved);
    }
}
