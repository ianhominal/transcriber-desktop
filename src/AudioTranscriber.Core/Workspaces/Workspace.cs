using System.Text.Json;

namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Carpeta de trabajo con la estructura:
/// <code>
/// [root]/
///   audios/               -&gt; audios sueltos (proyecto "General")
///     [Proyecto]/         -&gt; subcarpeta = proyecto, con sus audios
///       _proyecto.json    -&gt; metadata (título, descripción)
///   transcripts/          -&gt; .txt generados, espejando la estructura de audios/
/// </code>
/// </summary>
public sealed class Workspace
{
    private const string MetaFileName = "_proyecto.json";
    public const string GeneralName = "General";

    /// <summary>
    /// Extensiones de audio soportadas. Incluye <c>.webm</c> (Opus en contenedor WebM): es el
    /// formato que produce el navegador (MediaRecorder API) para las grabaciones hechas desde la
    /// web, que el sync baja tal cual a <c>audios/&lt;proyecto&gt;/&lt;stem&gt;.webm</c>
    /// (ver <see cref="Sync.SyncEngine"/>) -- changelog 2026-07-08: sin <c>.webm</c> acá,
    /// <see cref="ListAudiosIn"/> nunca reconocía ese archivo como audio real aunque estuviera
    /// correctamente descargado en disco, y la transcripción quedaba mostrada como huérfana/
    /// solo-texto con 0 KB y sin reproducir.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".ogg", ".opus", ".mp4", ".m4a", ".webm" };

    public string RootPath { get; }
    public string AudiosPath { get; }
    public string TranscriptsPath { get; }

    private Workspace(string rootPath)
    {
        RootPath = rootPath;
        AudiosPath = Path.Combine(rootPath, "audios");
        TranscriptsPath = Path.Combine(rootPath, "transcripts");
    }

    public static Workspace OpenOrCreate(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("La ruta del workspace no puede estar vacía.", nameof(rootPath));

        var ws = new Workspace(Path.GetFullPath(rootPath));
        Directory.CreateDirectory(ws.AudiosPath);
        Directory.CreateDirectory(ws.TranscriptsPath);
        ws.AdoptRootLevelProjectFolders();
        return ws;
    }

    /// <summary>Carpetas de la raíz que nunca se adoptan como proyecto (propias del cliente o ya son la estructura de proyectos).</summary>
    private static readonly IReadOnlySet<string> ReservedRootFolders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audios", "transcripts", ".synccache", ".papelera" };

    /// <summary>
    /// Adopta como proyecto cualquier subcarpeta pegada directamente en la RAÍZ del workspace (NO
    /// dentro de <see cref="AudiosPath"/>) que contenga audios soportados. Caso típico: el usuario
    /// arrastra <c>C:\MiCarpetaSync\Reunión\*.mp3</c> directo a la raíz en vez de
    /// <c>audios\Reunión\*.mp3</c>; hoy <see cref="ListProjects"/> solo mira dentro de
    /// <see cref="AudiosPath"/>, así que esa carpeta nunca se reconocía como proyecto ni sincronizaba.
    /// Comportamiento determinístico:
    /// <list type="bullet">
    /// <item>Solo mira archivos DIRECTOS de la subcarpeta (no recursivo), mismo criterio que <see cref="ListAudiosIn"/>.</item>
    /// <item>Ignora siempre <c>audios/</c>, <c>transcripts/</c>, <c>.synccache</c> y <c>.papelera</c> (para no reprocesarse a sí misma ni duplicar).</item>
    /// <item>Si la subcarpeta no tiene NINGÚN audio soportado, se ignora por completo: puede ser cualquier otra cosa que el usuario tenga ahí.</item>
    /// <item>Los audios se MUEVEN a <c>audios/&lt;NombreCarpeta&gt;</c> (se crea, o se fusiona si ya existe un proyecto con ese nombre); un archivo con el mismo nombre ya presente en el destino NO se sobrescribe (se asume ya sincronizado).</item>
    /// <item>Los archivos no-audio y las subcarpetas de esa carpeta NO se tocan. Si al terminar la carpeta original queda vacía, se borra (para no dejar un cascarón); si queda algo adentro, se deja como está.</item>
    /// </list>
    /// Se corre en cada <see cref="OpenOrCreate"/>: es idempotente, una vez migrados los audios no
    /// queda nada para adoptar en la siguiente vuelta.
    /// </summary>
    private void AdoptRootLevelProjectFolders()
    {
        if (!Directory.Exists(RootPath))
            return;

        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            var name = Path.GetFileName(dir);
            if (ReservedRootFolders.Contains(name))
                continue;

            var audioFiles = Directory.EnumerateFiles(dir)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                .ToList();
            if (audioFiles.Count == 0)
                continue; // no hay audios ahí: no es un proyecto, no se toca

            var safe = Sanitize(name);
            if (string.IsNullOrWhiteSpace(safe) || safe.Equals(GeneralName, StringComparison.OrdinalIgnoreCase))
                continue; // nombre inválido/reservado para proyecto: se deja la carpeta como está

            var destFolder = Path.Combine(AudiosPath, safe);
            Directory.CreateDirectory(destFolder);

            foreach (var audioFile in audioFiles)
            {
                var destPath = Path.Combine(destFolder, Path.GetFileName(audioFile));
                if (File.Exists(destPath))
                    continue; // ya existe en el proyecto (sync previo): no pisar, no duplicar
                File.Move(audioFile, destPath);
            }

            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    // ---- Transcripts -----------------------------------------------------

    /// <summary>
    /// Ruta del transcript (.txt) de un audio. <paramref name="projectFolder"/> es el nombre
    /// de la subcarpeta del proyecto (null para el proyecto General).
    /// </summary>
    public string TranscriptPathFor(string audioFileName, string? projectFolder = null)
    {
        var name = Path.GetFileNameWithoutExtension(audioFileName);
        var dir = string.IsNullOrEmpty(projectFolder)
            ? TranscriptsPath
            : Path.Combine(TranscriptsPath, projectFolder);
        return Path.Combine(dir, name + ".txt");
    }

    /// <summary>
    /// Guarda el contenido de un transcript (.txt) en <paramref name="transcriptPath"/>, creando
    /// la subcarpeta contenedora si todavía no existe. Hace falta porque <see cref="CreateProject"/>
    /// solo crea la subcarpeta bajo <see cref="AudiosPath"/>: la subcarpeta espejo bajo
    /// <see cref="TranscriptsPath"/> recién se crea la primera vez que se guarda un transcript ahí.
    /// </summary>
    public void SaveTranscript(string transcriptPath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
        File.WriteAllText(transcriptPath, text);
    }

    // ---- Audios ------------------------------------------------------------

    /// <summary>
    /// Ruta del archivo de audio de una transcripción. <paramref name="projectFolder"/> es el
    /// nombre de la subcarpeta del proyecto (null para el proyecto General). Misma convención
    /// de carpetas que <see cref="TranscriptPathFor"/>, pero bajo <see cref="AudiosPath"/> y
    /// conservando el nombre de archivo completo (con extensión).
    /// </summary>
    public string AudioPathFor(string audioFileName, string? projectFolder = null)
    {
        var dir = string.IsNullOrEmpty(projectFolder)
            ? AudiosPath
            : Path.Combine(AudiosPath, projectFolder);
        return Path.Combine(dir, audioFileName);
    }

    // ---- Listado ---------------------------------------------------------

    /// <summary>Audios sueltos en la raíz de audios/ (proyecto General). Compat con la versión previa.</summary>
    public IReadOnlyList<AudioItem> ListAudios() => ListAudiosIn(AudiosPath, projectFolder: null);

    /// <summary>
    /// Lista los proyectos: primero "General" (audios sueltos), luego cada subcarpeta,
    /// ordenados por nombre.
    /// </summary>
    public IReadOnlyList<AudioProject> ListProjects()
    {
        var projects = new List<AudioProject>
        {
            new AudioProject
            {
                Name = GeneralName,
                FolderPath = AudiosPath,
                IsGeneral = true,
                Title = GeneralName,
                Audios = ListAudiosIn(AudiosPath, projectFolder: null),
            },
        };

        if (Directory.Exists(AudiosPath))
        {
            foreach (var dir in Directory.EnumerateDirectories(AudiosPath)
                         .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                var (title, desc, color) = ReadMeta(dir, name);
                projects.Add(new AudioProject
                {
                    Name = name,
                    FolderPath = dir,
                    IsGeneral = false,
                    Title = title,
                    Description = desc,
                    Color = color,
                    Audios = ListAudiosIn(dir, projectFolder: name),
                });
            }
        }

        return projects;
    }

    private IReadOnlyList<AudioItem> ListAudiosIn(string folder, string? projectFolder)
    {
        var items = new List<AudioItem>();

        if (Directory.Exists(folder))
        {
            items.AddRange(Directory.EnumerateFiles(folder)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .Select(path =>
                {
                    var fileName = Path.GetFileName(path);
                    return new AudioItem
                    {
                        FileName = fileName,
                        FullPath = path,
                        TranscriptPath = TranscriptPathFor(fileName, projectFolder),
                        HasAudio = true,
                    };
                }));
        }

        items.AddRange(ListOrphanTranscriptsIn(projectFolder, items));

        return items.OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Transcripciones SOLO TEXTO: un .txt en transcripts/&lt;proyecto&gt; sin ningún audio
    /// soportado con el mismo nombre base en audios/&lt;proyecto&gt; (<paramref name="audiosYaListados"/>,
    /// ya relevados por el caller). Bug real que motiva esto: una transcripción bajada de la web
    /// sin audio (<c>audio_url_signed</c> null/vacío -- la subida del audio falló del lado del
    /// backend, pero <c>SyncEngine</c> igual escribe el .txt y la marca sincronizada) quedaba
    /// invisible PARA SIEMPRE en el desktop, porque este método (la única fuente de "qué hay en un
    /// proyecto" para toda la UI y el sync) solo enumeraba por archivo de audio. Ver changelog
    /// 2026-07-08.
    /// </summary>
    private IReadOnlyList<AudioItem> ListOrphanTranscriptsIn(string? projectFolder, IReadOnlyList<AudioItem> audiosYaListados)
    {
        var transcriptsFolder = string.IsNullOrEmpty(projectFolder)
            ? TranscriptsPath
            : Path.Combine(TranscriptsPath, projectFolder);

        if (!Directory.Exists(transcriptsFolder))
            return Array.Empty<AudioItem>();

        var stemsConAudio = new HashSet<string>(
            audiosYaListados.Select(a => Path.GetFileNameWithoutExtension(a.FileName)),
            StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateFiles(transcriptsFolder, "*.txt")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(stem => !string.IsNullOrEmpty(stem) && !stemsConAudio.Contains(stem!))
            .Select(stem => new AudioItem
            {
                FileName = stem!,
                FullPath = string.Empty,
                TranscriptPath = TranscriptPathFor(stem! + ".txt", projectFolder),
                HasAudio = false,
            })
            .ToList();
    }

    // ---- Operaciones de proyecto ----------------------------------------

    /// <summary>Crea (o devuelve) un proyecto con ese nombre.</summary>
    public AudioProject CreateProject(string name)
    {
        var safe = Sanitize(name);
        if (string.IsNullOrWhiteSpace(safe) || safe.Equals(GeneralName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Nombre de proyecto inválido.", nameof(name));

        var folder = Path.Combine(AudiosPath, safe);
        Directory.CreateDirectory(folder);
        return new AudioProject
        {
            Name = safe, FolderPath = folder, IsGeneral = false, Title = safe,
            Audios = Array.Empty<AudioItem>(),
        };
    }

    /// <summary>Renombra un proyecto (y su carpeta de transcripts).</summary>
    public void RenameProject(AudioProject project, string newName)
    {
        if (project.IsGeneral)
            throw new InvalidOperationException("No se puede renombrar el proyecto General.");
        var safe = Sanitize(newName);
        if (string.IsNullOrWhiteSpace(safe))
            throw new ArgumentException("Nombre inválido.", nameof(newName));

        var newFolder = Path.Combine(AudiosPath, safe);
        Directory.Move(project.FolderPath, newFolder);

        var oldT = Path.Combine(TranscriptsPath, project.Name);
        var newT = Path.Combine(TranscriptsPath, safe);
        if (Directory.Exists(oldT))
            Directory.Move(oldT, newT);
    }

    /// <summary>
    /// Borra un proyecto con sus audios y transcripts. NO es un borrado permanente: mueve todo a
    /// <c>.papelera/</c> (mismo mecanismo y misma carpeta que ya usa <see cref="Sync.SyncEngine"/>
    /// para los borrados que llegan de la nube), para paridad con la web y como red de seguridad
    /// contra un clic accidental. Para borrar de verdad sin pasar por la papelera, usar
    /// <see cref="DeleteProjectPermanently"/> explícitamente.
    /// </summary>
    public void DeleteProject(AudioProject project)
    {
        if (project.IsGeneral)
            throw new InvalidOperationException("No se puede borrar el proyecto General.");

        var bucket = PapeleraBucketFor(project.Name);

        if (Directory.Exists(project.FolderPath))
            Directory.Move(project.FolderPath, Path.Combine(bucket, "audios"));

        var t = Path.Combine(TranscriptsPath, project.Name);
        if (Directory.Exists(t))
            Directory.Move(t, Path.Combine(bucket, "transcripts"));
    }

    /// <summary>Borrado DEFINITIVO de un proyecto (sin pasar por la papelera). Acción explícita y separada: no la usa la UI por default.</summary>
    public void DeleteProjectPermanently(AudioProject project)
    {
        if (project.IsGeneral)
            throw new InvalidOperationException("No se puede borrar el proyecto General.");
        if (Directory.Exists(project.FolderPath))
            Directory.Delete(project.FolderPath, recursive: true);
        var t = Path.Combine(TranscriptsPath, project.Name);
        if (Directory.Exists(t))
            Directory.Delete(t, recursive: true);
    }

    /// <summary>Mueve un audio (y su transcript si existe) a otro proyecto.</summary>
    public void MoveAudio(AudioItem audio, AudioProject toProject)
    {
        if (!audio.HasAudio)
        {
            // Transcripción SOLO TEXTO (ver AudioItem.HasAudio): no hay archivo de audio real que
            // mover (audio.FullPath está vacío) -- solo el .txt.
            if (File.Exists(audio.TranscriptPath))
            {
                var destTranscriptOnly = TranscriptPathFor(audio.FileName, toProject.IsGeneral ? null : toProject.Name);
                if (!string.Equals(Path.GetFullPath(destTranscriptOnly), Path.GetFullPath(audio.TranscriptPath), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destTranscriptOnly)!);
                    File.Move(audio.TranscriptPath, destTranscriptOnly, overwrite: true);
                }
            }
            return;
        }

        var dest = Path.Combine(toProject.FolderPath, audio.FileName);
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(audio.FullPath), StringComparison.OrdinalIgnoreCase))
            return; // mismo lugar
        File.Move(audio.FullPath, dest, overwrite: false);

        if (File.Exists(audio.TranscriptPath))
        {
            var destTranscript = TranscriptPathFor(audio.FileName, toProject.IsGeneral ? null : toProject.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destTranscript)!);
            File.Move(audio.TranscriptPath, destTranscript, overwrite: true);
        }
    }

    /// <summary>Renombra el archivo de audio (y su transcript).</summary>
    public void RenameAudio(AudioItem audio, string newFileNameNoExt)
    {
        var safe = Sanitize(newFileNameNoExt);
        if (string.IsNullOrWhiteSpace(safe))
            throw new ArgumentException("Nombre inválido.", nameof(newFileNameNoExt));

        if (!audio.HasAudio)
        {
            // Transcripción SOLO TEXTO: no hay audio.FullPath real, solo se renombra el .txt.
            if (File.Exists(audio.TranscriptPath))
            {
                var newTranscriptOnly = Path.Combine(Path.GetDirectoryName(audio.TranscriptPath)!, safe + ".txt");
                File.Move(audio.TranscriptPath, newTranscriptOnly, overwrite: false);
            }
            return;
        }

        var ext = Path.GetExtension(audio.FileName);
        var folder = Path.GetDirectoryName(audio.FullPath)!;
        var dest = Path.Combine(folder, safe + ext);
        File.Move(audio.FullPath, dest, overwrite: false);

        if (File.Exists(audio.TranscriptPath))
        {
            var newTranscript = Path.Combine(Path.GetDirectoryName(audio.TranscriptPath)!, safe + ".txt");
            File.Move(audio.TranscriptPath, newTranscript, overwrite: true);
        }
    }

    /// <summary>
    /// Borra un audio y su transcript. NO es un borrado permanente: mueve ambos a
    /// <c>.papelera/</c> (mismo criterio que <see cref="DeleteProject"/>). Para borrar de verdad,
    /// usar <see cref="DeleteAudioPermanently"/> explícitamente.
    /// </summary>
    public void DeleteAudio(AudioItem audio)
    {
        if (!File.Exists(audio.FullPath) && !File.Exists(audio.TranscriptPath))
            return;

        var bucket = PapeleraBucketFor(Path.GetFileNameWithoutExtension(audio.FileName));

        if (File.Exists(audio.FullPath))
            File.Move(audio.FullPath, Path.Combine(bucket, audio.FileName));
        if (File.Exists(audio.TranscriptPath))
            File.Move(audio.TranscriptPath, Path.Combine(bucket, Path.GetFileName(audio.TranscriptPath)));
    }

    /// <summary>
    /// Borra una LISTA de audios de una (multi-select nativo de la vista de proyecto, ver
    /// <c>MainViewModel.DeleteSelectedFilesCommand</c>). Mismo destino que <see cref="DeleteAudio"/>
    /// para cada uno (<c>.papelera/</c>, nunca borrado permanente) — no hace falta que los audios
    /// estén sincronizados.
    /// </summary>
    public void DeleteAudios(IEnumerable<AudioItem> audios)
    {
        foreach (var audio in audios)
            DeleteAudio(audio);
    }

    /// <summary>Borrado DEFINITIVO de un audio (sin pasar por la papelera). Acción explícita y separada: no la usa la UI por default.</summary>
    public void DeleteAudioPermanently(AudioItem audio)
    {
        if (File.Exists(audio.FullPath))
            File.Delete(audio.FullPath);
        if (File.Exists(audio.TranscriptPath))
            File.Delete(audio.TranscriptPath);
    }

    /// <summary>
    /// Crea (si hace falta) y devuelve la carpeta bucket de <c>.papelera/&lt;timestamp&gt;_&lt;label&gt;/</c>
    /// donde mover lo borrado. Misma convención que <see cref="Sync.SyncEngine"/> usa para sus
    /// propios borrados-por-sync, para que ambos caminos de borrado terminen en el mismo lugar.
    /// </summary>
    private string PapeleraBucketFor(string label)
    {
        var safeLabel = string.Join("_", label.Split(Path.GetInvalidFileNameChars()));
        var bucket = Path.Combine(RootPath, ".papelera", $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{safeLabel}");
        Directory.CreateDirectory(bucket);
        return bucket;
    }

    // ---- Metadata de proyecto -------------------------------------------

    private static (string title, string description, string? color) ReadMeta(string folder, string fallbackTitle)
    {
        try
        {
            var metaPath = Path.Combine(folder, MetaFileName);
            if (File.Exists(metaPath))
            {
                var meta = JsonSerializer.Deserialize<ProjectMeta>(File.ReadAllText(metaPath));
                if (meta is not null)
                    return (string.IsNullOrWhiteSpace(meta.Title) ? fallbackTitle : meta.Title,
                            meta.Description ?? string.Empty,
                            // Ids desconocidos (versión futura de la app) o campo ausente
                            // (_proyecto.json viejo, de antes de esta feature) caen a null (sin
                            // color) sin tirar — mismo criterio defensivo que el resto de este
                            // método ante metadata corrupta.
                            ProjectColorPalette.Normalize(meta.Color));
            }
        }
        catch { /* metadata corrupta → usar defaults */ }
        return (fallbackTitle, string.Empty, null);
    }

    /// <summary>
    /// Lee solo el color persistido de un proyecto directamente desde su <c>_proyecto.json</c>,
    /// sin pasar por <see cref="ListProjects"/> (que reconstruye la lista completa). Público
    /// porque <see cref="Sync.SyncEngine"/> lo necesita para preservar el color local -- campo
    /// que el server no conoce, no viaja en el DTO de sync -- al reconstruir un
    /// <see cref="AudioProject"/> nuevo en un pull-upsert (ver
    /// <c>SyncEngine.ExecutePullProjectUpsert</c>). Reutiliza <see cref="ReadMeta"/> para no
    /// duplicar el try/catch defensivo: devuelve <c>null</c> si falta el archivo, falta el
    /// campo, o el JSON está corrupto (nunca tira).
    /// </summary>
    public string? ReadProjectColor(string projectFolderPath) => ReadMeta(projectFolderPath, fallbackTitle: string.Empty).color;

    /// <summary>Guarda título, descripción y color del proyecto en su _proyecto.json.</summary>
    public void SaveProjectMeta(AudioProject project)
    {
        if (project.IsGeneral)
            return; // General no lleva metadata
        var metaPath = Path.Combine(project.FolderPath, MetaFileName);
        var json = JsonSerializer.Serialize(
            new ProjectMeta
            {
                Title = project.Title,
                Description = project.Description,
                // Nunca persistir un id inválido/desconocido: si llegó corrupto desde la UI
                // (no debería, pero defensa en profundidad) se guarda como "sin color".
                Color = ProjectColorPalette.Normalize(project.Color),
            },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(metaPath, json);
    }

    /// <summary>
    /// Quita caracteres inválidos de un nombre de archivo/carpeta (reemplazados por espacio) y
    /// recorta bordes. Público porque el caller a veces necesita predecir la ruta final que va a
    /// producir <see cref="RenameAudio"/>/<see cref="CreateProject"/> (mismo criterio, una sola
    /// fuente de verdad para la sanitización).
    /// </summary>
    public static string Sanitize(string name)
    {
        name = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');
        return name.Trim();
    }

    private sealed class ProjectMeta
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Id de <see cref="ProjectColorPalette"/>, o <c>null</c>. Nullable string, ausente en
        /// _proyecto.json escritos antes de esta feature — System.Text.Json deja esta propiedad en
        /// su default (<c>null</c>) para ese caso, sin tirar (backward compat).
        /// </summary>
        public string? Color { get; set; }
    }
}
