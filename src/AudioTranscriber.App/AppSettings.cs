using System.IO;
using System.Text.Json;
using AudioTranscriber.Core.Sync;
using AudioTranscriber.Core.Transcription;

namespace AudioTranscriber.App;

/// <summary>
/// Configuración persistente de la app (motor elegido, modelo de Groq, modelo local, idioma).
/// Se guarda en %LOCALAPPDATA%\AudioTranscriber\settings.json.
/// Ya NO guarda ninguna API key de Groq: el modo nube transcribe vía el backend propio, que tiene
/// su propia key server-side (ver <see cref="AudioTranscriber.Core.Transcription.CloudTranscriptionService"/>).
/// </summary>
public sealed class AppSettings
{
    public string Engine { get; set; } = "local"; // "local" | "groq"
    public string Language { get; set; } = "es";
    public string GroqModel { get; set; } = "whisper-large-v3-turbo";

    /// <summary>
    /// Modelo GGML de Whisper que usa el motor Local: "small" | "medium" | "large-v3" (ver
    /// <see cref="LocalModelOptions"/> para el catálogo completo, tamaños reales y el fallback ante
    /// valores corruptos/desconocidos -- <see cref="LocalModelOptions.Resolve"/>). Default
    /// <see cref="LocalModelOptions.DefaultModelId"/> ("small") a propósito: es el único modelo que
    /// existía antes de este selector (2026-07-15), así que subir el default forzaría una descarga
    /// de cientos de MB no pedida a quien ya viene usando la app.
    /// </summary>
    public string LocalModelId { get; set; } = LocalModelOptions.DefaultModelId;

    public double Volume { get; set; } = 0.5;

    /// <summary>
    /// Modo de transcripción vía Groq: "transcribe" (normal, default) o "translate" (traduce el
    /// texto ya transcripto al idioma destino, ver <see cref="TranslationTargetLanguage"/> y
    /// <see cref="AudioTranscriber.Core.Transcription.TranslationOptions"/>). Solo tiene efecto con
    /// el motor nube (Groq) -- el motor Local (Whisper en la PC) no traduce.
    /// </summary>
    public string TranscribeMode { get; set; } = "transcribe";

    /// <summary>Idioma destino cuando <see cref="TranscribeMode"/> es "translate" (mismo código ISO
    /// corto que la allowlist del backend: es/en/pt/fr/it/de).</summary>
    public string TranslationTargetLanguage { get; set; } = "en";

    /// <summary>
    /// Identificar quién habla ("Persona 1"/"Persona 2"…) al transcribir con el motor Local,
    /// usando sherpa-onnx 100% offline (ver <see cref="AudioTranscriber.Core.Diarization.DiarizationService"/>).
    /// Solo funciona con ese motor -- la nube (Groq) no tiene esta función -- así que activarlo
    /// fuerza <see cref="Engine"/> a "local" (ver MainViewModel.OnUseDiarizationChanged).
    /// </summary>
    public bool UseDiarization { get; set; }

    /// <summary>
    /// La ÚNICA carpeta de la app: es el workspace (lo que ve MainWindow) Y la carpeta que se
    /// sincroniza con la nube, al estilo Dropbox. Reemplaza a los viejos <see cref="WorkspacePath"/>
    /// y <see cref="SyncFolderPath"/> (ver migración en <see cref="Load"/>). Vacío = todavía no
    /// se configuró ninguna carpeta (onboarding pendiente).
    /// </summary>
    public string SyncFolder { get; set; } = string.Empty;

    /// <summary>
    /// Nombre, email y foto de perfil del usuario logueado (ver <see cref="AuthUser.DisplayName"/>
    /// y <see cref="AuthUser.AvatarUrl"/> en Core). Se persisten al loguear para poder mostrarlos
    /// en SyncWindow sin volver a consultar Supabase; se limpian al cerrar sesión. No son secretos
    /// (a diferencia de los tokens, que viven cifrados en <see cref="SecureStore"/>), así que
    /// alcanza con guardarlos en claro acá.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    public string UserEmail { get; set; } = string.Empty;

    public string UserAvatarUrl { get; set; } = string.Empty;

    /// <summary>
    /// Campo legado (pre-unificación): carpeta de trabajo que abría MainWindow. Se conserva solo
    /// para poder migrar un <c>settings.json</c> viejo (ver <see cref="SyncFolderMigration"/>);
    /// no lo lea ni lo escriba código nuevo, use <see cref="SyncFolder"/>.
    /// </summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>Carpeta de exportación (vault de Obsidian o carpeta de Drive). Vacío = no exportar.</summary>
    public string ExportFolder { get; set; } = string.Empty;

    /// <summary>
    /// Campo legado (pre-unificación): carpeta que se elegía aparte en la ventana de sync. Se
    /// conserva solo para migración (ver <see cref="SyncFolderMigration"/>); no lo lea ni lo
    /// escriba código nuevo, use <see cref="SyncFolder"/>.
    /// </summary>
    public string SyncFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Espejo del estado de inicio automático con Windows, solo para reflejar la UI sin releer
    /// el registro en cada refresco. La fuente de verdad real es el registro
    /// (ver <see cref="AutoStartHelper"/>); esto se resincroniza contra ella al arrancar.
    /// </summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>
    /// Si al cerrar la ventana principal con la [x] la app se minimiza a la bandeja (default) en
    /// vez de cerrarse de verdad. Ver <see cref="AudioTranscriber.Core.Runtime.WindowCloseBehavior"/>
    /// (Core, puro y testeado) para el mapeo de este setting a la acción real que toma
    /// App.xaml.cs.OnMainWindowClosing.
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Si el nombre del .md exportado lleva prefijo de fecha (yyyy-MM-dd - ...).</summary>
    public bool ExportDateInName { get; set; } = true;

    /// <summary>
    /// Tema visual: "Light" | "Dark" | "System" (default). Se lee con
    /// <see cref="AudioTranscriber.Core.Runtime.ThemeResolver.Parse"/> (cualquier valor
    /// desconocido/corrupto cae a System) y se aplica al arrancar y al cambiar el selector en
    /// Configuración (ver <see cref="AudioTranscriber.App.ThemeManager"/>).
    /// </summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Ids (rutas de transcript locales) de notas descartadas en la card de "Resurfacing" (ver
    /// <see cref="AudioTranscriber.Core.Notes.ResurfaceCandidatePicker"/>, brief "Híbrido nativo"
    /// 2026-07-14): equivalente nativo del <c>localStorage</c> que usa la web para persistir
    /// descartes entre sesiones (la card es puramente client-side, sin endpoint nuevo).
    /// </summary>
    public List<string> ResurfaceDismissedIds { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioTranscriber", "settings.json");

    private static AppSettings? _instance;

    /// <summary>
    /// Instancia única compartida en memoria: MainViewModel, SyncCoordinator y TrayIconService
    /// leen y escriben esta MISMA instancia (se carga del disco una sola vez), en vez de cada uno
    /// tener su propia copia independiente. Antes de la unificación de carpeta cada consumidor
    /// llamaba a <see cref="Load"/> por su cuenta; con un solo campo (<see cref="SyncFolder"/>)
    /// compartido entre varios, copias independientes podían pisarse entre sí al guardar (uno
    /// guarda la carpeta nueva, otro guarda su copia vieja encima). Usar la misma instancia
    /// elimina esa clase de bug de raíz.
    /// </summary>
    public static AppSettings Instance => _instance ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                // Migración: unifica los dos campos viejos (WorkspacePath / SyncFolderPath) en
                // uno solo. Ver SyncFolderMigration para el criterio de prioridad.
                s.SyncFolder = SyncFolderMigration.Resolve(s.SyncFolder, s.SyncFolderPath, s.WorkspacePath);
                return s;
            }
        }
        catch { /* archivo corrupto → arrancamos con defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* si no se puede guardar, no es fatal */ }
    }
}
