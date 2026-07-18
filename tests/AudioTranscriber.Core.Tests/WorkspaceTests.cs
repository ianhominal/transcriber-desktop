using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.Tests;

public class WorkspaceTests : IDisposable
{
    private readonly string _root;

    public WorkspaceTests()
    {
        // Carpeta temporal aislada por test-run
        _root = Path.Combine(Path.GetTempPath(), "at_tests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void OpenOrCreate_creates_audios_and_transcripts_subfolders()
    {
        var ws = Workspace.OpenOrCreate(_root);

        Assert.True(Directory.Exists(ws.AudiosPath));
        Assert.True(Directory.Exists(ws.TranscriptsPath));
        Assert.Equal(Path.Combine(_root, "audios"), ws.AudiosPath);
        Assert.Equal(Path.Combine(_root, "transcripts"), ws.TranscriptsPath);
    }

    [Fact]
    public void ListAudios_returns_only_supported_audio_files()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "uno.mp3"), "");
        File.WriteAllText(Path.Combine(ws.AudiosPath, "dos.wav"), "");
        File.WriteAllText(Path.Combine(ws.AudiosPath, "leeme.txt"), ""); // no es audio
        File.WriteAllText(Path.Combine(ws.AudiosPath, "video.mkv"), ""); // formato no soportado

        var audios = ws.ListAudios();

        Assert.Equal(2, audios.Count);
        Assert.Contains(audios, a => a.FileName == "uno.mp3");
        Assert.Contains(audios, a => a.FileName == "dos.wav");
    }

    [Fact]
    public void ListAudios_flags_audios_that_already_have_a_transcript()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "hecho.mp3"), "");
        File.WriteAllText(Path.Combine(ws.AudiosPath, "pendiente.mp3"), "");
        // transcript de "hecho.mp3" -> "hecho.txt"
        File.WriteAllText(Path.Combine(ws.TranscriptsPath, "hecho.txt"), "contenido");

        var audios = ws.ListAudios();

        var hecho = audios.Single(a => a.FileName == "hecho.mp3");
        var pendiente = audios.Single(a => a.FileName == "pendiente.mp3");
        Assert.True(hecho.HasTranscript);
        Assert.False(pendiente.HasTranscript);
    }

    [Fact]
    public void TranscriptPathFor_maps_audio_to_txt_in_transcripts_folder()
    {
        var ws = Workspace.OpenOrCreate(_root);

        var path = ws.TranscriptPathFor("charla.mp3");

        Assert.Equal(Path.Combine(ws.TranscriptsPath, "charla.txt"), path);
    }

    [Fact]
    public void SaveTranscript_creates_project_transcripts_subfolder_when_missing()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Test");
        var transcriptPath = ws.TranscriptPathFor("audio.mp3", project.Name);

        // CreateProject solo crea audios/Test: la subcarpeta espejo bajo transcripts/ todavía
        // no existe (esto es lo que hacía fallar "Guardar" con "Could not find a part of the path").
        Assert.False(Directory.Exists(Path.GetDirectoryName(transcriptPath)));

        ws.SaveTranscript(transcriptPath, "contenido de prueba");

        Assert.True(File.Exists(transcriptPath));
        Assert.Equal("contenido de prueba", File.ReadAllText(transcriptPath));
    }

    [Fact]
    public void TranscriptPathFor_conProjectFolder_ubicaElTxtDentroDeLaCarpetaDelProyecto()
    {
        // Regresión del bug de MainViewModel.TranscribeAsync: llamar a TranscriptPathFor SIN
        // projectFolder (aunque el audio pertenezca a un proyecto) manda el .txt a la raíz de
        // transcripts/ en vez de transcripts/<project>/. El fix real vive en el ViewModel (usa
        // audio.TranscriptPath, ya calculado con projectFolder por ListAudiosIn); este test fija
        // el contrato de Workspace del que depende ese fix.
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Entrevistas");

        var pathConProyecto = ws.TranscriptPathFor("charla.mp3", project.Name);
        var pathSinProyecto = ws.TranscriptPathFor("charla.mp3");

        Assert.Equal(Path.Combine(ws.TranscriptsPath, "Entrevistas", "charla.txt"), pathConProyecto);
        Assert.NotEqual(pathSinProyecto, pathConProyecto);
    }

    [Fact]
    public void OpenOrCreate_CarpetaConAudiosPegadaEnLaRaiz_SeAdoptaComoProyectoDentroDeAudios()
    {
        // Bug: el usuario arrastra "<root>\Reunion\*.mp3" directo a la raíz de la carpeta
        // sincronizada en vez de "<root>\audios\Reunion\*.mp3". ListProjects() solo mira dentro de
        // audios/, así que esa carpeta nunca se reconocía como proyecto.
        var loosePath = Path.Combine(_root, "Reunion");
        Directory.CreateDirectory(loosePath);
        File.WriteAllText(Path.Combine(loosePath, "charla.mp3"), "audio-bytes");

        var ws = Workspace.OpenOrCreate(_root);

        var project = ws.ListProjects().Single(p => p.Name == "Reunion");
        Assert.False(project.IsGeneral);
        Assert.Single(project.Audios);
        Assert.Equal("charla.mp3", project.Audios[0].FileName);
        Assert.Equal(Path.Combine(ws.AudiosPath, "Reunion", "charla.mp3"), project.Audios[0].FullPath);

        // La carpeta original quedó vacía -> se borra, no queda cascarón en la raíz.
        Assert.False(Directory.Exists(loosePath));
    }

    [Fact]
    public void OpenOrCreate_CarpetaEnLaRaizSinAudios_NoSeAdoptaComoProyecto()
    {
        var loosePath = Path.Combine(_root, "Documentos");
        Directory.CreateDirectory(loosePath);
        File.WriteAllText(Path.Combine(loosePath, "notas.txt"), "no es audio");

        var ws = Workspace.OpenOrCreate(_root);

        Assert.DoesNotContain(ws.ListProjects(), p => p.Name == "Documentos");
        Assert.True(Directory.Exists(loosePath));
        Assert.True(File.Exists(Path.Combine(loosePath, "notas.txt")));
    }

    [Fact]
    public void OpenOrCreate_CarpetaEnLaRaizConAudiosYOtrosArchivos_MigraSoloAudiosYDejaElRestoIntacto()
    {
        var loosePath = Path.Combine(_root, "Mixta");
        Directory.CreateDirectory(loosePath);
        File.WriteAllText(Path.Combine(loosePath, "audio.mp3"), "audio-bytes");
        File.WriteAllText(Path.Combine(loosePath, "notas.txt"), "no es audio");

        var ws = Workspace.OpenOrCreate(_root);

        var project = ws.ListProjects().Single(p => p.Name == "Mixta");
        Assert.Single(project.Audios);
        // Queda algo adentro (notas.txt) -> la carpeta original NO se borra.
        Assert.True(Directory.Exists(loosePath));
        Assert.True(File.Exists(Path.Combine(loosePath, "notas.txt")));
        Assert.False(File.Exists(Path.Combine(loosePath, "audio.mp3")));
    }

    [Fact]
    public void OpenOrCreate_CarpetasInternasReservadas_NuncaSeAdoptanComoProyecto()
    {
        // .synccache y .papelera son del propio cliente; si alguien les mete audios NO deben
        // tratarse como proyecto (mismo criterio que SyncWatchFilter).
        var syncCache = Path.Combine(_root, ".synccache");
        Directory.CreateDirectory(syncCache);
        File.WriteAllText(Path.Combine(syncCache, "cache.mp3"), "audio-bytes");

        var papelera = Path.Combine(_root, ".papelera");
        Directory.CreateDirectory(papelera);
        File.WriteAllText(Path.Combine(papelera, "borrado.mp3"), "audio-bytes");

        var ws = Workspace.OpenOrCreate(_root);

        Assert.DoesNotContain(ws.ListProjects(), p => p.Name is ".synccache" or ".papelera");
        Assert.True(File.Exists(Path.Combine(syncCache, "cache.mp3")));
        Assert.True(File.Exists(Path.Combine(papelera, "borrado.mp3")));
    }

    [Fact]
    public void OpenOrCreate_EsIdempotente_SegundaVueltaNoRompeNiDuplica()
    {
        var loosePath = Path.Combine(_root, "Reunion");
        Directory.CreateDirectory(loosePath);
        File.WriteAllText(Path.Combine(loosePath, "charla.mp3"), "audio-bytes");

        Workspace.OpenOrCreate(_root);
        var ws2 = Workspace.OpenOrCreate(_root);

        var project = ws2.ListProjects().Single(p => p.Name == "Reunion");
        Assert.Single(project.Audios);
    }

    // ---- DeleteProject / DeleteAudio: papelera en vez de borrado permanente ------------------
    // Regresión de la auditoría 2026-07-07: la UI hacía hard-delete mientras el mecanismo de
    // papelera (.papelera/) ya existía para los borrados que llegan de la nube. Paridad con la
    // web (que sí tiene papelera) + red de seguridad contra un clic accidental.

    [Fact]
    public void DeleteProject_MueveAudiosYTranscriptsAPapelera_NoBorraPermanente()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Entrevistas");
        File.WriteAllText(Path.Combine(project.FolderPath, "charla.mp3"), "audio-bytes");
        var transcriptsFolder = Path.Combine(ws.TranscriptsPath, "Entrevistas");
        Directory.CreateDirectory(transcriptsFolder);
        File.WriteAllText(Path.Combine(transcriptsFolder, "charla.txt"), "contenido");

        ws.DeleteProject(project);

        // La carpeta original del proyecto ya no está...
        Assert.False(Directory.Exists(project.FolderPath));
        Assert.False(Directory.Exists(transcriptsFolder));
        Assert.DoesNotContain(ws.ListProjects(), p => p.Name == "Entrevistas");

        // ...pero el contenido sigue existiendo dentro de .papelera/, no se perdió.
        var papeleraRoot = Path.Combine(_root, ".papelera");
        Assert.True(Directory.Exists(papeleraRoot));
        var bucket = Directory.EnumerateDirectories(papeleraRoot).Single();
        Assert.True(File.Exists(Path.Combine(bucket, "audios", "charla.mp3")));
        Assert.True(File.Exists(Path.Combine(bucket, "transcripts", "charla.txt")));
    }

    [Fact]
    public void DeleteAudio_MueveAudioYTranscriptAPapelera_NoBorraPermanente()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "suelto.mp3"), "audio-bytes");
        File.WriteAllText(Path.Combine(ws.TranscriptsPath, "suelto.txt"), "contenido");
        var audio = ws.ListAudios().Single(a => a.FileName == "suelto.mp3");

        ws.DeleteAudio(audio);

        Assert.False(File.Exists(audio.FullPath));
        Assert.False(File.Exists(audio.TranscriptPath));

        var papeleraRoot = Path.Combine(_root, ".papelera");
        var bucket = Directory.EnumerateDirectories(papeleraRoot).Single();
        Assert.True(File.Exists(Path.Combine(bucket, "suelto.mp3")));
        Assert.True(File.Exists(Path.Combine(bucket, "suelto.txt")));
    }

    [Fact]
    public void DeleteAudio_SinTranscript_MueveSoloElAudio()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "sinTranscript.mp3"), "audio-bytes");
        var audio = ws.ListAudios().Single(a => a.FileName == "sinTranscript.mp3");

        ws.DeleteAudio(audio);

        Assert.False(File.Exists(audio.FullPath));
        var papeleraRoot = Path.Combine(_root, ".papelera");
        var bucket = Directory.EnumerateDirectories(papeleraRoot).Single();
        Assert.True(File.Exists(Path.Combine(bucket, "sinTranscript.mp3")));
    }

    [Fact]
    public void DeleteAudios_MueveTodaLaListaAPapelera_DejaElRestoIntacto()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "uno.mp3"), "audio-bytes");
        File.WriteAllText(Path.Combine(ws.TranscriptsPath, "uno.txt"), "contenido");
        File.WriteAllText(Path.Combine(ws.AudiosPath, "dos.mp3"), "audio-bytes");
        File.WriteAllText(Path.Combine(ws.AudiosPath, "tresIntacto.mp3"), "audio-bytes");

        var audios = ws.ListAudios();
        var aBorrar = audios.Where(a => a.FileName is "uno.mp3" or "dos.mp3").ToList();

        ws.DeleteAudios(aBorrar);

        var restantes = ws.ListAudios();
        var intacto = Assert.Single(restantes);
        Assert.Equal("tresIntacto.mp3", intacto.FileName);
        Assert.True(File.Exists(intacto.FullPath));

        // Los dos borrados (uno con transcript, uno sin) terminaron en .papelera/, no se perdieron.
        var papeleraRoot = Path.Combine(_root, ".papelera");
        var buckets = Directory.EnumerateDirectories(papeleraRoot).ToList();
        Assert.Contains(buckets, b => File.Exists(Path.Combine(b, "uno.mp3")) && File.Exists(Path.Combine(b, "uno.txt")));
        Assert.Contains(buckets, b => File.Exists(Path.Combine(b, "dos.mp3")));
    }

    [Fact]
    public void DeleteAudios_ListaVacia_NoHaceNada()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "intacto.mp3"), "audio-bytes");

        ws.DeleteAudios(Array.Empty<AudioItem>());

        Assert.Single(ws.ListAudios());
        Assert.False(Directory.Exists(Path.Combine(_root, ".papelera")));
    }

    [Fact]
    public void DeleteProjectPermanently_BorraDeVerdadSinPasarPorPapelera()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Descartable");
        File.WriteAllText(Path.Combine(project.FolderPath, "a.mp3"), "audio-bytes");

        ws.DeleteProjectPermanently(project);

        Assert.False(Directory.Exists(project.FolderPath));
        Assert.False(Directory.Exists(Path.Combine(_root, ".papelera")));
    }

    [Fact]
    public void DeleteAudioPermanently_BorraDeVerdadSinPasarPorPapelera()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "descartable.mp3"), "audio-bytes");
        var audio = ws.ListAudios().Single(a => a.FileName == "descartable.mp3");

        ws.DeleteAudioPermanently(audio);

        Assert.False(File.Exists(audio.FullPath));
        Assert.False(Directory.Exists(Path.Combine(_root, ".papelera")));
    }

    // ---- Transcripciones SOLO TEXTO (bug: invisibles para siempre en desktop) ----------------
    // Bug real: una transcripción bajada de la web sin audio (audio_url_signed null/vacío, ver
    // SyncEngine) escribe el .txt pero antes NUNCA aparecía en ListProjects()/ListAudios() porque
    // ListAudiosIn solo enumeraba por archivo de audio. Ver changelog 2026-07-08.

    [Fact]
    public void ListAudios_TxtSinAudioCorrespondiente_SeListaComoItemSinAudio()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(ws.TranscriptPathFor("solo-texto.mp3"), "transcripción sin audio");

        var audios = ws.ListAudios();

        var item = Assert.Single(audios);
        Assert.Equal("solo-texto", item.FileName);
        Assert.False(item.HasAudio);
        Assert.Equal(string.Empty, item.FullPath);
        Assert.True(item.HasTranscript);
    }

    [Fact]
    public void ListAudios_TxtConAudioCorrespondiente_NoSeDuplicaComoItemSinAudio()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(Path.Combine(ws.AudiosPath, "hecho.mp3"), "audio-bytes");
        File.WriteAllText(ws.TranscriptPathFor("hecho.mp3"), "contenido");

        var audios = ws.ListAudios();

        var item = Assert.Single(audios);
        Assert.True(item.HasAudio);
        Assert.Equal("hecho.mp3", item.FileName);
    }

    [Fact]
    public void ListProjects_TxtSoloTextoDentroDeUnProyecto_SeListaDentroDeEseProyecto()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Entrevistas");
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Entrevistas"));
        File.WriteAllText(ws.TranscriptPathFor("nota.mp3", "Entrevistas"), "texto sin audio");

        var refreshed = ws.ListProjects().Single(p => p.Name == "Entrevistas");

        var item = Assert.Single(refreshed.Audios);
        Assert.False(item.HasAudio);
        Assert.Equal("nota", item.FileName);
    }

    [Fact]
    public void MoveAudio_ItemSinAudio_MueveSoloElTxt()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var target = ws.CreateProject("Destino");
        File.WriteAllText(ws.TranscriptPathFor("solo-texto.mp3"), "texto");
        var item = ws.ListAudios().Single(a => a.FileName == "solo-texto");

        ws.MoveAudio(item, target);

        Assert.False(File.Exists(ws.TranscriptPathFor("solo-texto.mp3")));
        var moved = ws.ListProjects().Single(p => p.Name == "Destino").Audios.Single();
        Assert.False(moved.HasAudio);
        Assert.Equal("texto", File.ReadAllText(moved.TranscriptPath));
    }

    [Fact]
    public void RenameAudio_ItemSinAudio_RenombraSoloElTxt()
    {
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(ws.TranscriptPathFor("original.mp3"), "texto");
        var item = ws.ListAudios().Single(a => a.FileName == "original");

        ws.RenameAudio(item, "renombrado");

        Assert.False(File.Exists(ws.TranscriptPathFor("original.mp3")));
        var renamed = ws.ListAudios().Single();
        Assert.False(renamed.HasAudio);
        Assert.Equal("renombrado", renamed.FileName);
    }

    // ---- Audio descargado en .webm no se asociaba a su transcripción (0 KB / sin play) -------
    // Bug real (evidencia de disco): el sync baja los .webm correctamente a audios/<proyecto>/,
    // pero SupportedExtensions no incluía ".webm", así que ListAudiosIn nunca los reconocía como
    // audio real -- el .txt correspondiente quedaba mostrado como huérfano/solo-texto (HasAudio
    // = false, FullPath vacío), aunque el archivo de audio SÍ existiera en disco. Ver changelog
    // 2026-07-08.

    [Fact]
    public void Webm_EsUnaExtensionDeAudioSoportada()
    {
        Assert.Contains(".webm", Workspace.SupportedExtensions);
    }

    [Fact]
    public void ListAudios_WebmConTranscripcionCorrespondiente_SeAsociaComoAudioReal()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var audioBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }; // contenido no vacío -> tamaño > 0
        File.WriteAllBytes(Path.Combine(ws.AudiosPath, "Grabacion-123.webm"), audioBytes);
        File.WriteAllText(ws.TranscriptPathFor("Grabacion-123.webm"), "transcripción bajada de la web");

        var audios = ws.ListAudios();

        var item = Assert.Single(audios);
        Assert.Equal("Grabacion-123.webm", item.FileName);
        Assert.True(item.HasAudio);
        Assert.True(item.HasTranscript);
        Assert.NotEqual(string.Empty, item.FullPath);
        Assert.True(File.Exists(item.FullPath));
        Assert.True(new FileInfo(item.FullPath).Length > 0);
    }

    [Fact]
    public void ListProjects_WebmDentroDeUnProyecto_SeAsociaComoAudioRealEnEseProyecto()
    {
        var ws = Workspace.OpenOrCreate(_root);
        var project = ws.CreateProject("Reuniones");
        File.WriteAllBytes(Path.Combine(project.FolderPath, "Reunion-456.webm"), new byte[] { 9, 9, 9 });
        Directory.CreateDirectory(Path.Combine(ws.TranscriptsPath, "Reuniones"));
        File.WriteAllText(ws.TranscriptPathFor("Reunion-456.webm", "Reuniones"), "texto de la reunión");

        var refreshed = ws.ListProjects().Single(p => p.Name == "Reuniones");

        var item = Assert.Single(refreshed.Audios);
        Assert.True(item.HasAudio);
        Assert.Equal("Reunion-456.webm", item.FileName);
        Assert.True(new FileInfo(item.FullPath).Length > 0);
    }

    [Fact]
    public void ListAudios_TxtSinWebmCorrespondiente_SigueVisibleComoSoloTexto()
    {
        // No debe romperse el fix v1.0.14: una transcripción cuyo audio NUNCA se descargó (o el
        // backend nunca lo tuvo) sigue apareciendo en la lista, ahora con HasAudio = false y sin
        // play -- el criterio de "aparece en la lista" sigue siendo el .txt.
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllText(ws.TranscriptPathFor("Grabacion-789.webm"), "transcripción sin audio_url_signed");

        var audios = ws.ListAudios();

        var item = Assert.Single(audios);
        Assert.Equal("Grabacion-789", item.FileName);
        Assert.False(item.HasAudio);
        Assert.Equal(string.Empty, item.FullPath);
        Assert.True(item.HasTranscript);
    }

    [Fact]
    public void ListAudios_MezclaDeExtensionesDeAudioPorStem_SoloElWebmSeAsocia()
    {
        // Correlación por stem cubriendo varias extensiones a la vez: solo el .webm que coincide
        // con la transcripción se asocia; los demás quedan como audios propios, sin transcript.
        var ws = Workspace.OpenOrCreate(_root);
        File.WriteAllBytes(Path.Combine(ws.AudiosPath, "audio-a.webm"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(ws.AudiosPath, "audio-b.wav"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(ws.AudiosPath, "audio-c.mp3"), new byte[] { 3 });
        File.WriteAllText(ws.TranscriptPathFor("audio-a.webm"), "transcripción de a");

        var audios = ws.ListAudios();

        Assert.Equal(3, audios.Count);
        var a = audios.Single(x => x.FileName == "audio-a.webm");
        Assert.True(a.HasAudio);
        Assert.True(a.HasTranscript);
        Assert.True(audios.Single(x => x.FileName == "audio-b.wav").HasAudio);
        Assert.False(audios.Single(x => x.FileName == "audio-b.wav").HasTranscript);
        Assert.True(audios.Single(x => x.FileName == "audio-c.mp3").HasAudio);
    }
}
