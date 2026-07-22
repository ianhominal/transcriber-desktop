using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AudioTranscriber.App; // ClipboardService (CopyTranscript, ver comentario ahí)
using AudioTranscriber.Core.Audio;
using AudioTranscriber.Core.Common;
using AudioTranscriber.Core.Diarization;
using AudioTranscriber.Core.Export;
using AudioTranscriber.Core.Notes;
using AudioTranscriber.Core.Sync;
using AudioTranscriber.Core.Transcription;
using AudioTranscriber.Core.Updates;
using AudioTranscriber.Core.Workspaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AudioTranscriber.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly string _modelDir;
    private readonly AppSettings _settings;
    private Workspace? _workspace;
    private TranscriptionService? _service;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// True mientras <see cref="TranscribeProjectAsync"/> está corriendo (FEATURE 2, 2026-07-17).
    /// Evita un segundo click en "Transcribir" durante los huecos entre audios del lote, donde
    /// <see cref="IsBusy"/> vuelve a false por un instante (cada audio maneja su propio IsBusy en
    /// <see cref="TranscribeSelectedAudioAsync"/>, sin cambios de esa lógica — ver el comentario
    /// largo de TranscribeProjectAsync).
    /// </summary>
    private bool _isBatchTranscribing;

    /// <summary>
    /// Se prende desde <see cref="Cancel"/> (botón "Cancelar") y lo lee <see cref="TranscribeProjectAsync"/>
    /// para cortar el lote entre un audio y el siguiente — <see cref="_cts"/> ya se cancela y se
    /// libera por cada audio individual (ver TranscribeSelectedAudioAsync), así que por sí solo no
    /// alcanza para saber si había un lote en curso que además haya que frenar.
    /// </summary>
    private bool _batchCancelRequested;

    /// <summary>
    /// Único punto de verdad sobre el modelo GGML local (Whisper) SELECCIONADO: existe/no existe en
    /// disco, y cómo descargarlo. Se comparte con <see cref="TranscriptionService"/> (reusado, no
    /// uno nuevo por sesión) y con <see cref="DownloadModelCommand"/>, que ahora es el ÚNICO camino
    /// explícito para bajar el modelo -- ver comentario largo en <see cref="DownloadModelAsync"/>
    /// sobre el bug de UX que esto resuelve (2026-07-14: "se pone a bajar el modelo mientras
    /// grabás"). NO es <c>readonly</c>: <c>OnLocalModelIdChanged</c> lo reconstruye apuntando al
    /// modelo nuevo cada vez que cambia el selector (2026-07-15, ver esa región más abajo) -- cada
    /// modelo es un archivo GGML distinto, así que ModelPath/IsModelAvailable tienen que reflejar
    /// el elegido, no uno fijo.
    /// </summary>
    private WhisperModelProvider _modelProvider;

    /// <summary>
    /// Único punto de verdad sobre los DOS modelos de identificación de hablantes (sherpa-onnx,
    /// ver <see cref="DiarizationModelProvider"/>): existen/no existen en disco, y cómo bajarlos.
    /// Misma carpeta base que <see cref="_modelProvider"/> (ver comentario ahí) y mismo patrón de
    /// comando propio de descarga (<see cref="DownloadDiarizationModelsCommand"/>).
    /// </summary>
    private readonly DiarizationModelProvider _diarizationModelProvider;

    /// <summary>Corre el modelo de identificación de hablantes sobre el WAV que ya convirtió
    /// <see cref="_service"/> -- ver la región "Identificar quién habla" más abajo.</summary>
    private readonly DiarizationService _diarizationService;

    // Cronómetro en vivo del trabajo actual (se refresca cada segundo).
    private readonly Stopwatch _elapsedSw = new();
    private readonly DispatcherTimer _elapsedTimer;

    // Vigila la carpeta 'audios' para refrescar la lista automáticamente.
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _refreshDebounce;

    // Reproductor para escuchar el audio seleccionado (soporta OGG/Opus y WebM/Opus).
    private readonly AudioPlayer _player = new();
    private readonly DispatcherTimer _playbackTimer;
    private bool _isSeeking; // true mientras el usuario arrastra la barra de progreso

    // Grabación desde el micrófono (botón Grabar/Detener).
    private MicrophoneRecorder? _recorder;
    private readonly Stopwatch _recordingSw = new();
    private readonly DispatcherTimer _recordingTimer;

    // Grabación de reunión (botón "Grabar reunión"): micrófono + audio del sistema. Comparte
    // cronómetro/VU meter con la grabación normal (_recordingSw/_recordingTimer de arriba) -- ver
    // la región "Grabar audio desde el micrófono" para el detalle de por qué es el mismo camino.
    private MeetingRecorder? _meetingRecorder;

    /// <summary>Carpeta donde vive el modelo GGML local (Whisper.net).</summary>
    public string ModelDir => _modelDir;

    public MainViewModel()
    {
        // El modelo se guarda una vez en %LOCALAPPDATA%\AudioTranscriber\models
        _modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioTranscriber", "models");
        // AppSettings.Instance acá (en vez de esperar a _settings = ... más abajo): es el mismo
        // singleton en memoria, pedirlo un poco antes no tiene costo ni efecto secundario, y
        // _modelProvider necesita el modelo ELEGIDO desde el arranque (no siempre Small).
        var localModel = LocalModelOptions.Resolve(AppSettings.Instance.LocalModelId);
        _localModelId = localModel.Id;
        _modelProvider = new WhisperModelProvider(_modelDir, localModel.Type);
        // Misma carpeta base que el modelo de Whisper (ver comentario en el campo): un solo lugar
        // para todos los modelos locales de la app.
        _diarizationModelProvider = new DiarizationModelProvider(_modelDir);
        _diarizationService = new DiarizationService(_diarizationModelProvider);

        _settings = AppSettings.Instance;
        _engine = _settings.Engine;
        _language = _settings.Language;
        _groqModel = _settings.GroqModel;
        _transcribeMode = _settings.TranscribeMode;
        _translationTargetLanguage = _settings.TranslationTargetLanguage;
        _useDiarization = _settings.UseDiarization;
        _volume = _settings.Volume;
        _player.Volume = (float)_settings.Volume;
        _exportFolder = _settings.ExportFolder;

        // El árbol se refresca solo cuando un sync (automático o manual) bajó cambios remotos.
        SyncCoordinator.Instance.SyncApplied += OnSyncApplied;

        // ShowSearchResults depende de SearchResults.Count, que no dispara PropertyChanged por sí
        // solo en el sentido que necesita una propiedad COMPUTADA (ObservableCollection sí levanta
        // su propio "Count", pero no el de ShowSearchResults) -- se reenvía acá.
        SearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowSearchResults));

        // Banner "Hay una actualización disponible" (ver MainWindow.xaml): se prende cuando
        // UpdateService ya descargó una versión nueva, sea por el chequeo automático de
        // App.OnStartup o por el manual desde SettingsWindow. Mismo evento que ya escucha
        // TrayIconService (el ítem de la bandeja se sigue mostrando también, no se saca).
        UpdateService.Instance.UpdateReady += OnUpdateReady;

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedText = _elapsedSw.Elapsed.ToString(@"mm\:ss");

        // Timer que refresca la posición de reproducción (para la barra de progreso).
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += (_, _) => UpdatePlaybackPosition();
        _player.PlaybackEnded += () => _elapsedTimer.Dispatcher.Invoke(StopAudio);

        // Debounce: agrupa varios cambios de archivo en un solo refresh.
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshDebounce.Tick += (_, _) => { _refreshDebounce.Stop(); RefreshAudios(); };

        // Cronómetro de grabación (independiente del de transcripción): se refresca cada segundo.
        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordingTimer.Tick += (_, _) => RecordingElapsedText = TimeFormatter.Format(_recordingSw.Elapsed);

        // Reabrir la última carpeta usada (si sigue existiendo): es la MISMA carpeta que se
        // sincroniza (ver SyncCoordinator), ya no hay dos conceptos separados.
        //
        // Bugfix 2026-07-10 (HIGH: app invisible con --minimized). LoadWorkspace hace I/O síncrono
        // (Workspace.OpenOrCreate -> AdoptRootLevelProjectFolders -> File.Move) y
        // SyncCoordinator.Start arranca un FileSystemWatcher -- sin try/catch acá, una
        // IOException/UnauthorizedAccessException (carpeta de nube offline, archivo bloqueado por
        // antivirus/reproductor, placeholder de OneDrive/Google Drive todavía no descargado) se
        // propagaba fuera del ctor, fuera de `new MainWindow()` en App.xaml.cs, cuyo catch pone
        // `window = null` y por lo tanto NUNCA crea el ícono de bandeja -- con `--minimized`, la
        // app quedaba corriendo sin ventana NI bandeja (invisible total, solo visible en el
        // Administrador de tareas). Mismo patrón defensivo que ya usan OpenWorkspace y
        // AddDroppedFiles más abajo: se atrapa acá, se informa en StatusMessage y se loguea, y la
        // ventana termina de construirse igual -- el usuario puede reintentar desde la UI.
        if (!string.IsNullOrWhiteSpace(_settings.SyncFolder) && Directory.Exists(_settings.SyncFolder))
        {
            try
            {
                LoadWorkspace(_settings.SyncFolder);
                SyncCoordinator.Instance.Start(_settings.SyncFolder);
            }
            catch (Exception ex)
            {
                AudioTranscriber.App.CrashLogger.Log(ex);
                StatusMessage = $"No se pudo abrir tu carpeta: {ex.Message}";
            }
        }
    }

    /// <summary>Refresca el árbol de proyectos cuando un sync bajó cambios remotos.</summary>
    private void OnSyncApplied()
    {
        if (_workspace is not null)
            RefreshAudios();
    }

    // ---- Banner de actualización ("Hay una actualización disponible") ---------------------

    /// <summary>True cuando hay una actualización ya descargada, lista para instalar (banner visible).</summary>
    [ObservableProperty]
    private bool _isUpdateReady;

    /// <summary>Texto del banner ("Hay una actualización disponible (X.Y.Z).").</summary>
    [ObservableProperty]
    private string _updateBannerText = string.Empty;

    /// <summary>
    /// UpdateService.UpdateReady puede dispararse tanto desde el chequeo automático en background
    /// (App.OnStartup) como desde el manual de SettingsWindow: marshaleamos al Dispatcher por las
    /// dudas, mismo criterio (y mismo bug real documentado) que TrayIconService.OnUpdateReady.
    /// </summary>
    private void OnUpdateReady(string newVersion)
    {
        if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => OnUpdateReady(newVersion));
            return;
        }
        UpdateBannerText = UpdateUiTextFormatter.FormatBannerText(newVersion);
        IsUpdateReady = true;
    }

    /// <summary>Llamado desde el botón "Reiniciar y actualizar" del banner.</summary>
    [RelayCommand]
    private void ApplyUpdate() => UpdateService.Instance.ApplyAndRestart();

    /// <summary>
    /// Onboarding: si no hay carpeta configurada todavía, guía login -&gt; elegir carpeta. Si ya
    /// está configurada, no hace nada (ya se cargó en el constructor). Se llama desde
    /// <c>MainWindow.Loaded</c> (no desde el constructor: abrir diálogos ahí es riesgoso, la
    /// ventana todavía no terminó de armarse).
    /// </summary>
    public void EnsureOnboarded()
    {
        if (!string.IsNullOrWhiteSpace(_settings.SyncFolder) && Directory.Exists(_settings.SyncFolder))
            return;

        if (!SyncCoordinator.Instance.EnsureLoggedIn())
        {
            StatusMessage = "Iniciá sesión desde 'Sincronización' para configurar tu carpeta.";
            return;
        }

        var path = SyncCoordinator.Instance.ChooseFolderAndStart();
        if (path is not null)
            LoadWorkspace(path);
        else
            StatusMessage = "Elegí tu carpeta para empezar.";
    }

    /// <summary>Carpeta de trabajo por defecto (cuando se arrastra un archivo sin haber elegido una).</summary>
    public static string DefaultWorkspacePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioTranscriber");

    /// <summary>Motor de transcripción: "local" (Whisper en tu PC) o "groq" (nube).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroq))]
    [NotifyPropertyChangedFor(nameof(IsTranslateMode))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadModelBanner))]
    [NotifyPropertyChangedFor(nameof(TranscribeDisabledReason))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand))]
    private string _engine = "local";

    /// <summary>Modelo de Groq: turbo (rápido) o large-v3 (máxima calidad). Se manda al backend,
    /// que es quien tiene la API key de Groq -- ver <see cref="CloudTranscriptionService"/>.</summary>
    [ObservableProperty]
    private string _groqModel = "whisper-large-v3-turbo";

    // Diálogos simples reutilizables.
    private static string PromptText(string title, string prompt, string defaultValue)
        => InputDialog.Show(title, prompt, defaultValue) ?? string.Empty;

    private static bool Confirm(string message)
        => MessageBox.Show(message, "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;

    /// <summary>True si el motor elegido es la nube (Groq vía backend).</summary>
    public bool IsGroq => string.Equals(Engine, "groq", StringComparison.OrdinalIgnoreCase);

    partial void OnGroqModelChanged(string value)
    {
        _settings.GroqModel = value;
        _settings.Save();
    }

    partial void OnEngineChanged(string value)
    {
        _settings.Engine = value;
        _settings.Save();
    }

    // ---- Identificar quién habla (diarización, sherpa-onnx, 100% local) ----------------------

    /// <summary>
    /// Si está activo, después de transcribir con el motor Local se identifica quién habla y el
    /// texto queda marcado "Persona 1:"/"Persona 2:"… (ver <see cref="DiarizationService"/> y
    /// <see cref="AudioTranscriber.Core.Diarization.SpeakerAssigner"/> en Core, y el uso en
    /// <see cref="TranscribeAsync"/>). La nube (Groq) no tiene esta función: activarlo FUERZA el
    /// motor a Local y lo deja fijo mientras siga activo -- ver <see cref="OnUseDiarizationChanged"/>
    /// y <see cref="IsEngineSelectable"/>/<see cref="EngineLockedReason"/>, bindeados al selector
    /// de motor en MainWindow.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEngineSelectable))]
    [NotifyPropertyChangedFor(nameof(EngineLockedReason))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadDiarizationModelsBanner))]
    [NotifyCanExecuteChangedFor(nameof(DownloadDiarizationModelsCommand))]
    private bool _useDiarization;

    partial void OnUseDiarizationChanged(bool value)
    {
        _settings.UseDiarization = value;
        _settings.Save();

        // La nube no identifica hablantes: activar esto fuerza el motor a Local. Reasignar Engine
        // acá dispara OnEngineChanged (persiste) y los NotifyPropertyChangedFor ya encadenados en
        // esa propiedad -- misma idea que EngineSelector.Decide más abajo en TranscribeAsync, pero
        // acá la decisión es incondicional (no depende del tamaño del archivo).
        if (value && Engine != "local")
            Engine = "local";
    }

    /// <summary>True si el selector de motor se puede tocar. False mientras "Identificar quién
    /// habla" está activo: en ese caso el motor queda fijo en Local (ver OnUseDiarizationChanged),
    /// así que dejarlo elegible sería mostrar una opción que en los hechos no hace nada.</summary>
    public bool IsEngineSelectable => !UseDiarization;

    /// <summary>Motivo por el que el selector de motor está bloqueado en MainWindow, o null si se
    /// puede elegir libremente (mismo patrón que <see cref="TranscribeDisabledReason"/>).</summary>
    public string? EngineLockedReason => UseDiarization
        ? "Con \"Identificar quién habla\" activado en Configuración, el motor queda fijo en Local: la nube (Groq) no puede identificar hablantes."
        : null;

    // ---- Selector de calidad del modelo local (2026-07-15) ------------------------------------
    //
    // Hasta acá el motor Local SIEMPRE usaba GgmlType.Small (167 MB, cuantizado Q5_0) -- el modelo
    // más chico de Whisper, sin forma de elegir otro. Un usuario real reportó errores de
    // reconocimiento en español ("Acompanianos" sin ñ, "Esplota", "TaySports") que se explican
    // justamente por eso. Este selector le da al motor Local la misma libertad de elegir calidad
    // que ya tenía la nube (ver GroqModel más arriba) -- catálogo completo (ids/labels/tamaños
    // reales/fallback) en LocalModelOptions (Core), ComboBox "Modelo:" en MainWindow.xaml (junto al
    // de "Motor:", visible solo con motor Local).

    /// <summary>
    /// Id del modelo local elegido ("small"/"medium"/"large-v3", ver <see cref="LocalModelOptions"/>
    /// para el catálogo completo). Default <see cref="LocalModelOptions.DefaultModelId"/> a
    /// propósito -- ver comentario en <see cref="AppSettings.LocalModelId"/>: cambiar el default
    /// forzaría una descarga no pedida a quien ya viene usando la app.
    /// </summary>
    [ObservableProperty]
    private string _localModelId = LocalModelOptions.DefaultModelId;

    /// <summary>
    /// Reconstruye <see cref="_modelProvider"/> apuntando al modelo recién elegido y suelta
    /// <see cref="_service"/> (si ya existía) para que la próxima transcripción lo recree con el
    /// modelo correcto -- ver el comentario largo en el campo <see cref="_modelProvider"/>. Sin
    /// esto, el selector cambiaría solo lo que MUESTRA la UI sin cambiar qué se descarga/transcribe
    /// de verdad (se seguiría usando el modelo viejo aunque el combo ya muestre el nuevo).
    /// </summary>
    partial void OnLocalModelIdChanged(string value)
    {
        var resolved = LocalModelOptions.Resolve(value);
        _settings.LocalModelId = resolved.Id;
        _settings.Save();

        // Cada modelo (Rápido/Bueno/El mejor) es un archivo GGML distinto -- esto es lo que hace
        // que ModelPath/IsModelAvailable (y por lo tanto IsLocalModelAvailable/
        // ShowDownloadModelBanner/TranscribeDisabledReason de más abajo) reflejen el modelo RECIÉN
        // elegido en vez de uno fijo.
        _modelProvider = new WhisperModelProvider(_modelDir, resolved.Type);

        // _service (si ya se había creado) quedó atado al _modelProvider VIEJO y, si ya se
        // transcribió algo en esta sesión, con el modelo VIEJO ya cargado en memoria -- ver
        // TranscriptionService, "la WhisperFactory se carga una sola vez y se reutiliza", y
        // TranscribeAsync más abajo ("_service ??= new TranscriptionService(_modelProvider)").
        // Soltarlo entero (no solo reasignar _modelProvider) es necesario: si no, la PRÓXIMA
        // transcripción seguiría usando el modelo anterior aunque la UI ya muestre el nuevo. La
        // liberación en sí es best-effort y no bloquea el hilo de UI -- ver ReleaseServiceAsync. El
        // combo que setea esta propiedad queda deshabilitado mientras IsBusy (ver MainWindow.xaml)
        // para que este cambio nunca compita con una transcripción en curso en otro hilo.
        var previousService = _service;
        _service = null;
        if (previousService is not null)
            _ = ReleaseServiceAsync(previousService);

        OnPropertyChanged(nameof(IsLocalModelAvailable));
        OnPropertyChanged(nameof(ShowDownloadModelBanner));
        OnPropertyChanged(nameof(TranscribeDisabledReason));
        TranscribeCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Libera (best-effort) el <see cref="TranscriptionService"/> atado al modelo local
    /// anterior -- ver <see cref="OnLocalModelIdChanged"/>. Nunca tira: perder la referencia sin
    /// llamar DisposeAsync igual libera la memoria eventualmente vía GC, no vale la pena romper el
    /// flujo por un fallo acá.</summary>
    private static async Task ReleaseServiceAsync(TranscriptionService service)
    {
        try
        {
            await service.DisposeAsync();
        }
        catch
        {
            // best-effort, ver comentario del método.
        }
    }

    // ---- Descarga del modelo local (separada de grabar/transcribir, 2026-07-14) --------------
    //
    // DIAGNÓSTICO del bug reportado ("si tengo el modelo local se pone a bajarlo mientras
    // grabás"): antes NO existía ningún paso explícito de "descargar modelo". La descarga vivía
    // escondida adentro de TranscribeAsync -- WhisperModelProvider.EnsureModelAsync se llamaba
    // recién ahí, mezclada en el mismo Progress/IsBusy que la transcripción en sí. Como
    // StopRecordingAsync ofrece "¿Transcribirla ahora?" apenas termina de grabar y, si se acepta,
    // llama a TranscribeAsync DIRECTO, la primera vez que alguien graba con el motor Local sin
    // tener el modelo todavía, la descarga (que puede tardar varios minutos) arrancaba
    // AUTOMÁTICAMENTE pegada al final de grabar, sin ningún aviso previo ni paso separado -- de
    // ahí la sensación de "se pone a bajar mientras grabás". El motor/calidad/idioma tampoco eran
    // visibles en la pantalla principal (viven en Configuración desde F4), así que nada advertía
    // de entrada que el motor Local necesitaba ese paso previo.
    //
    // FIX: la descarga ahora es un comando propio (DownloadModelCommand), con su propia barra de
    // progreso (misma IsDownloading/DownloadPercent de siempre, pero ya NO se dispara desde
    // TranscribeAsync) y un botón explícito en MainWindow ("Descargar modelo") que solo aparece
    // cuando el motor Local está elegido y el modelo todavía no está. TranscribeAsync ahora
    // RECHAZA transcribir con el motor Local si el modelo no está disponible (ver el guard al
    // principio del método), tanto si se dispara desde el botón "Transcribir" (CanTranscribe ya lo
    // bloquea) como si se dispara directo desde StopRecordingAsync (ese guard interno es la
    // defensa real). La lógica de descarga EN SÍ (WhisperModelProvider.EnsureModelAsync, dónde/
    // cómo baja el modelo) no se tocó -- lo que cambió es cuándo/cómo se dispara y se presenta.

    /// <summary>True si el modelo GGML local ya está en disco (chequeo en vivo, sin caché).</summary>
    public bool IsLocalModelAvailable => _modelProvider.IsModelAvailable;

    /// <summary>
    /// True cuando corresponde mostrar el aviso "Descargá el modelo antes de grabar/transcribir"
    /// (ver Card en MainWindow.xaml, arriba del botón Transcribir): motor Local elegido, el modelo
    /// todavía no está, y no hay una descarga ya en curso (para no duplicar el aviso con la barra
    /// de progreso que ya se ve en el footer mientras <see cref="IsDownloading"/> es true).
    /// </summary>
    public bool ShowDownloadModelBanner => !IsGroq && !IsLocalModelAvailable && !IsDownloading;

    private bool CanDownloadModel() => !IsBusy && !IsDownloading && !IsLocalModelAvailable;

    /// <summary>
    /// Único camino para bajar el modelo local: explícito (botón propio), previo a grabar/
    /// transcribir, con su propia barra de progreso -- ver el diagnóstico completo arriba de esta
    /// región. Reusa IsBusy/IsDownloading/DownloadPercent (mismas propiedades que ya bindea el
    /// footer) para no duplicar UI de progreso, y el mismo CancelCommand (CanCancel ya depende de
    /// IsBusy) para poder cancelar una descarga larga sin agregar un botón nuevo.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadModel))]
    private async Task DownloadModelAsync()
    {
        IsBusy = true;
        IsDownloading = true;
        DownloadPercent = 0;
        StatusMessage = "Descargando modelo local (única vez, no hace falta grabar ni transcribir para esto)…";
        _cts = new CancellationTokenSource();

        var downloadProgress = new Progress<ModelDownloadProgress>(p =>
        {
            DownloadPercent = p.Percent;
            StatusMessage = p.TotalBytes > 0
                ? $"Descargando modelo (única vez): {p.MegabytesReceived:0} / {p.TotalMegabytes:0} MB ({p.Percent:0}%)"
                : $"Descargando modelo (única vez): {p.MegabytesReceived:0} MB…";
        });

        try
        {
            await _modelProvider.EnsureModelAsync(downloadProgress, _cts.Token);
            StatusMessage = "Modelo local listo. Ya podés grabar y transcribir sin conexión.";
            SystemSounds.Asterisk.Play();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Descarga del modelo cancelada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo descargar el modelo: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
            DownloadPercent = 0;
            _cts?.Dispose();
            _cts = null;

            OnPropertyChanged(nameof(IsLocalModelAvailable));
            OnPropertyChanged(nameof(ShowDownloadModelBanner));
            OnPropertyChanged(nameof(TranscribeDisabledReason));
            TranscribeCommand.NotifyCanExecuteChanged();
            DownloadModelCommand.NotifyCanExecuteChanged();
        }
    }

    // ---- Descarga de los modelos de identificación de hablantes -------------------------------
    //
    // Mismo criterio que la región de arriba (comando propio, explícito, con su propia barra de
    // progreso): la diferencia es que ACÁ no bloqueamos "Transcribir" si faltan -- a propósito.
    // Identificar quién habla es un paso OPCIONAL sobre una transcripción que de por sí ya
    // funciona sin él; si se bloqueara "Transcribir" hasta bajar estos modelos, alguien que
    // activó el toggle sin darse cuenta se quedaría sin poder transcribir NADA por un problema en
    // un extra. En cambio, TranscribeAsync sigue de largo igual y avisa si no pudo etiquetar (ver
    // TryLabelSpeakersAsync) -- el banner de acá abajo es solo un empujón para bajarlos ANTES.

    /// <summary>True cuando los DOS modelos de identificación de hablantes ya están en disco.</summary>
    public bool IsDiarizationModelAvailable => _diarizationModelProvider.IsModelAvailable;

    /// <summary>
    /// True cuando corresponde avisar que faltan los modelos para identificar quién habla: la
    /// función está activada, todavía falta alguno de los dos modelos, y no hay una descarga en
    /// curso (mismo criterio que <see cref="ShowDownloadModelBanner"/>, ver comentario ahí).
    /// </summary>
    public bool ShowDownloadDiarizationModelsBanner =>
        UseDiarization && !IsDiarizationModelAvailable && !IsDownloading;

    private bool CanDownloadDiarizationModels() => !IsBusy && !IsDownloading && !IsDiarizationModelAvailable;

    /// <summary>
    /// Baja el/los modelo/s de identificación de hablantes que falten. Reusa IsBusy/IsDownloading/
    /// DownloadPercent (mismas propiedades del footer) y el mismo CancelCommand que
    /// <see cref="DownloadModelAsync"/> -- son descargas chicas (decenas de MB, no 1,5 GB), pero
    /// el patrón es el mismo.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadDiarizationModels))]
    private async Task DownloadDiarizationModelsAsync()
    {
        IsBusy = true;
        IsDownloading = true;
        DownloadPercent = 0;
        StatusMessage = "Descargando los modelos para identificar quién habla (chicos: son decenas de MB, no minutos de espera)…";
        _cts = new CancellationTokenSource();

        var downloadProgress = new Progress<ModelDownloadProgress>(p =>
        {
            DownloadPercent = p.Percent;
            StatusMessage = p.TotalBytes > 0
                ? $"Descargando modelos para identificar hablantes: {p.MegabytesReceived:0} / {p.TotalMegabytes:0} MB ({p.Percent:0}%)"
                : $"Descargando modelos para identificar hablantes: {p.MegabytesReceived:0} MB…";
        });

        try
        {
            await _diarizationModelProvider.EnsureModelAsync(downloadProgress, _cts.Token);
            StatusMessage = "Listo. Ya se puede identificar quién habla en tus próximas transcripciones.";
            SystemSounds.Asterisk.Play();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Descarga de los modelos de identificación de hablantes cancelada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudieron descargar los modelos para identificar hablantes: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
            DownloadPercent = 0;
            _cts?.Dispose();
            _cts = null;

            OnPropertyChanged(nameof(IsDiarizationModelAvailable));
            OnPropertyChanged(nameof(ShowDownloadDiarizationModelsBanner));
            DownloadDiarizationModelsCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Modo de transcripción vía Groq: "transcribe" (normal) o "translate" (transcribir y
    /// traducir, ver <see cref="TranslationTargetLanguage"/>). Sin efecto con el motor Local (ver
    /// <see cref="TranscribeAsync"/>: solo se le manda al backend cuando <see cref="IsGroq"/>).</summary>
    [ObservableProperty]
    private string _transcribeMode = "transcribe";

    /// <summary>Idioma destino cuando <see cref="TranscribeMode"/> es "translate" -- mismo código
    /// ISO corto que la allowlist del backend (ver <see cref="TranslationOptions"/>).</summary>
    [ObservableProperty]
    private string _translationTargetLanguage = TranslationOptions.DefaultLanguage;

    /// <summary>True cuando corresponde traducir: modo "translate" Y motor nube (el motor Local no
    /// traduce -- traducir el texto resultante requiere el mismo backend que ya usa Groq).</summary>
    public bool IsTranslateMode => TranslationOptions.IsTranslateMode(TranscribeMode) && IsGroq;

    partial void OnTranscribeModeChanged(string value)
    {
        _settings.TranscribeMode = value;
        _settings.Save();
        OnPropertyChanged(nameof(IsTranslateMode));
    }

    partial void OnTranslationTargetLanguageChanged(string value)
    {
        _settings.TranslationTargetLanguage = value;
        _settings.Save();
    }

    /// <summary>Tiempo transcurrido del trabajo actual (cronómetro en vivo).</summary>
    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string? _workspacePath;

    /// <summary>Expone el coordinador de sync compartido, para bindear estado en MainWindow.xaml.</summary>
    public SyncCoordinator Sync => SyncCoordinator.Instance;

    /// <summary>Árbol de proyectos (cada uno con sus audios).</summary>
    public ObservableCollection<ProjectVm> Projects { get; } = new();

    /// <summary>Proyecto actualmente enfocado (para agregar audios / crear subproyectos).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditProjectMetaCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProjectAssistantCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    // FEATURE 2 (2026-07-17): CanTranscribe() ahora también depende de SelectedProject (batch-
    // transcribir un proyecto sin audio puntual elegido, ver TranscribeProjectAsync) — sin esto,
    // el botón "Transcribir" quedaba con el estado viejo hasta que ALGÚN otro evento no
    // relacionado disparara un refresco del CanExecute.
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedProjectDescription))]
    [NotifyPropertyChangedFor(nameof(ShowProjectFilesView))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyPlaceholder))]
    [NotifyPropertyChangedFor(nameof(AddDestinationLabel))]
    [NotifyPropertyChangedFor(nameof(TranscribeDisabledReason))]
    private ProjectVm? _selectedProject;

    /// <summary>Descripción del proyecto seleccionado (para el encabezado).</summary>
    public string SelectedProjectDescription => SelectedProject?.Description ?? string.Empty;

    /// <summary>
    /// "A qué proyecto va" (rediseño 2026-07-14, paridad con el "Proyecto destino" de la web): se
    /// muestra arriba de "Cargar archivo(s)"/"Grabar audio" para que quede explícito ANTES de
    /// agregar o grabar un audio, en vez de que el destino sea un detalle implícito de qué nodo
    /// estaba enfocado en el árbol. Mismo proyecto que ya usan AddDroppedFiles/StartRecording
    /// (SelectedProject?.Model.FolderPath ?? _workspace.AudiosPath) -- esto solo lo hace VISIBLE,
    /// no cambia a qué carpeta van los archivos.
    ///
    /// Investigado 2026-07-14 (reporte "Va a: New test" confuso mientras el árbol mostraba
    /// General/grabado): NO es un bug de sincronización -- el binding es directo a
    /// <see cref="SelectedProject"/> (ver [NotifyPropertyChangedFor] arriba) y SelectedProject se
    /// actualiza en el mismo tick que la selección real del TreeView (OnTreeSelectionChanged). El
    /// caso reportado era un proyecto REALMENTE seleccionado pero fuera de la parte visible del
    /// árbol (scrolleado u otro nodo con foco visual), y el texto viejo ("Va a: X", chico y suelto)
    /// no alcanzaba para que se notara/confiara. Fix: presentación más grande y en su propio chip
    /// (ver MainWindow.xaml), no lógica.
    /// </summary>
    public string AddDestinationLabel => SelectedProject is { IsGeneral: false }
        ? $"Se guardará en: {SelectedProject.Title}"
        : "Se guardará en: General";

    /// <summary>
    /// True cuando el árbol tiene un PROYECTO seleccionado y NINGÚN audio (F3, "vista de
    /// proyecto"): el panel derecho muestra el listado de archivos del proyecto en vez del
    /// placeholder vacío o el editor de transcripción. Pasa a false apenas se selecciona un audio
    /// (dentro del mismo proyecto o de otro), ver <see cref="OnTreeSelectionChanged"/>.
    /// </summary>
    public bool ShowProjectFilesView => SelectedProject is not null && SelectedAudio is null;

    /// <summary>
    /// True cuando corresponde mostrar "Todavía no hay transcripción" en el panel derecho: nada
    /// seleccionado, o un audio seleccionado que todavía no tiene transcript. Reemplaza el trigger
    /// original (bindeado directo a "TranscriptText == ''"), agregando la excepción de
    /// <see cref="ShowProjectFilesView"/> para que la vista de proyecto no quede tapada por este
    /// placeholder.
    /// </summary>
    public bool ShowEmptyPlaceholder => !ShowProjectFilesView && string.IsNullOrEmpty(TranscriptText);

    /// <summary>Registro de actividad (qué está haciendo, con hora). Diagnóstico en vivo.</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveTranscriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAudioCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenNoteDetailCommand))]
    [NotifyPropertyChangedFor(nameof(ShowProjectFilesView))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyPlaceholder))]
    [NotifyPropertyChangedFor(nameof(TranscribeDisabledReason))]
    private AudioItemVm? _selectedAudio;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyTranscriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveTranscriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportNoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInObsidianCommand))]
    [NotifyCanExecuteChangedFor(nameof(PolishTextCommand))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyPlaceholder))]
    [NotifyPropertyChangedFor(nameof(AlreadyPolished))]
    [NotifyPropertyChangedFor(nameof(PolishButtonText))]
    [NotifyPropertyChangedFor(nameof(PolishTooltip))]
    private string _transcriptText = string.Empty;

    // ==== Vista Leer / Editar del transcript (paridad con la web, ver TranscriptReader) ====

    /// <summary>
    /// Modo de la vista del transcript: <c>true</c> = Leer (documento renderizado, con hablantes
    /// separados por color); <c>false</c> = Editar (el TextBox de siempre). Arranca en Leer porque
    /// casi siempre se viene a LEER, no a tocar el texto. Editar es deliberadamente el mismo TextBox
    /// y no un editor rico: el texto tiene que poder volver a texto plano para pulir/sincronizar.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowTranscriptAsReadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowTranscriptAsEditCommand))]
    private bool _transcriptReadMode = true;

    /// <summary>Turnos parseados para la vista Leer (ver <see cref="RefreshTranscriptBlocks"/>).</summary>
    public ObservableCollection<SpeakerBlockVm> TranscriptBlocks { get; } = new();

    // El texto cambió (se abrió otra nota, se pulió, se editó y se volvió a Leer): si estamos
    // leyendo, re-armar los turnos. En modo Editar la vista Leer no está a la vista, así que no
    // hace falta parsear en cada tecla.
    partial void OnTranscriptTextChanged(string value)
    {
        if (TranscriptReadMode)
            RefreshTranscriptBlocks();
    }

    partial void OnTranscriptReadModeChanged(bool value)
    {
        if (value)
            RefreshTranscriptBlocks();
    }

    // El botón del modo ACTIVO queda deshabilitado (no hay nada que cambiar): la UI usa ese estado
    // para resaltarlo, no para apagarlo. Un solo estilo sirve para los dos botones del toggle.
    [RelayCommand(CanExecute = nameof(CanShowTranscriptAsRead))]
    private void ShowTranscriptAsRead() => TranscriptReadMode = true;
    private bool CanShowTranscriptAsRead() => !TranscriptReadMode;

    [RelayCommand(CanExecute = nameof(CanShowTranscriptAsEdit))]
    private void ShowTranscriptAsEdit() => TranscriptReadMode = false;
    private bool CanShowTranscriptAsEdit() => TranscriptReadMode;

    /// <summary>
    /// Re-arma <see cref="TranscriptBlocks"/> desde <see cref="TranscriptText"/>. Con hablantes
    /// (transcripción diarizada del desktop) parte en turnos con su color; sin hablantes, un solo
    /// bloque con el documento entero. Mismo criterio que la vista Leer de la web.
    /// </summary>
    private void RefreshTranscriptBlocks()
    {
        TranscriptBlocks.Clear();

        var parsed = SpeakerTranscriptParser.Parse(TranscriptText);
        if (parsed is null)
        {
            TranscriptBlocks.Add(new SpeakerBlockVm(false, string.Empty, TranscriptText ?? string.Empty, SpeakerColorAssigner.NeutralSlot));
            return;
        }

        var colors = SpeakerColorAssigner.Assign(parsed.Select(b => b.Label));
        foreach (var b in parsed)
            TranscriptBlocks.Add(new SpeakerBlockVm(true, b.Label.ToUpperInvariant(), b.Text, colors[b.Label]));
    }

    /// <summary>Título opcional para la nota (encabezado + nombre del archivo .md).</summary>
    [ObservableProperty]
    private string _noteTitle = string.Empty;

    /// <summary>Contexto/notas opcionales que el usuario agrega sobre el audio.</summary>
    [ObservableProperty]
    private string _noteContext = string.Empty;

    /// <summary>Carpeta de exportación (vault de Obsidian o carpeta de Drive).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportNoteCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInObsidianCommand))]
    private string _exportFolder = string.Empty;

    partial void OnExportFolderChanged(string value)
    {
        _settings.ExportFolder = value ?? string.Empty;
        _settings.Save();
    }


    [ObservableProperty]
    private string _statusMessage = "Elegí tu carpeta para empezar.";

    /// <summary>
    /// Conteo de audios ("N audio(s) en M proyecto(s)."), en su propio canal (footer, junto a
    /// StatusMessage) -- ver A.4, DESIGN-REVIEW-2026-07-16.md. Antes vivía en StatusMessage, y el
    /// FileSystemWatcher (ver <see cref="_refreshDebounce"/>) lo pisaba a los 400ms exactamente en
    /// el caso de una importación parcial ("N ignorado(s) por formato no soportado", ver
    /// <see cref="AddDroppedFiles"/>), que es cuando más hacía falta leerlo. Vacío cuando no hay
    /// audios todavía: ese caso ya lo cubre el empty state del árbol (MainWindow.xaml) y el
    /// StatusMessage de onboarding de <see cref="RefreshAudios"/>.
    /// </summary>
    [ObservableProperty]
    private string _inventoryText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkspaceCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadDiarizationModelsCommand))]
    [NotifyPropertyChangedFor(nameof(IsTranscribing))]
    [NotifyPropertyChangedFor(nameof(TranscribeIndeterminate))]
    [NotifyPropertyChangedFor(nameof(TaskbarState))]
    [NotifyPropertyChangedFor(nameof(TranscribeDisabledReason))]
    private bool _isBusy;

    /// <summary>Ocupado transcribiendo (no descargando): barra indeterminada.</summary>
    public bool IsTranscribing => IsBusy && !IsDownloading;

    // ---- Progreso en la barra de tareas de Windows ----------------------

    /// <summary>Valor 0–1 para la barra de tareas (descarga o transcripción).</summary>
    public double TaskbarProgress => (IsDownloading ? DownloadPercent : TranscribePercent) / 100.0;

    /// <summary>Estado de la barra de tareas: normal (con %), indeterminado o ninguno.</summary>
    public System.Windows.Shell.TaskbarItemProgressState TaskbarState =>
        !IsBusy ? System.Windows.Shell.TaskbarItemProgressState.None
        : TaskbarProgress > 0 ? System.Windows.Shell.TaskbarItemProgressState.Normal
        : System.Windows.Shell.TaskbarItemProgressState.Indeterminate;

    /// <summary>Idioma del audio: "es" (español) o "auto".</summary>
    [ObservableProperty]
    private string _language = "es";

    partial void OnLanguageChanged(string value)
    {
        _settings.Language = value;
        _settings.Save();
    }

    /// <summary>True mientras se descarga el modelo (barra determinada con %).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranscribing))]
    [NotifyPropertyChangedFor(nameof(TranscribeIndeterminate))]
    [NotifyPropertyChangedFor(nameof(TaskbarProgress))]
    [NotifyPropertyChangedFor(nameof(TaskbarState))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadModelBanner))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadDiarizationModelsBanner))]
    [NotifyCanExecuteChangedFor(nameof(DownloadModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadDiarizationModelsCommand))]
    private bool _isDownloading;

    /// <summary>Porcentaje de descarga del modelo (0–100).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarProgress))]
    [NotifyPropertyChangedFor(nameof(TaskbarState))]
    private double _downloadPercent;

    /// <summary>Porcentaje real de la transcripción (0–100), según el avance en el audio.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscribeIndeterminate))]
    [NotifyPropertyChangedFor(nameof(TaskbarProgress))]
    [NotifyPropertyChangedFor(nameof(TaskbarState))]
    private double _transcribePercent;

    /// <summary>
    /// Barra indeterminada solo mientras se prepara (convierte + carga el modelo),
    /// es decir cuando transcribe pero todavía no hay ningún segmento (0%).
    /// </summary>
    public bool TranscribeIndeterminate => IsTranscribing && TranscribePercent <= 0;

    partial void OnSelectedAudioChanged(AudioItemVm? value)
    {
        // Al seleccionar, si ya tiene transcript lo mostramos; si no, limpiamos.
        //
        // `_loadedTranscriptText` guarda lo que se acaba de leer del disco: es el baseline contra el
        // que `IsTranscriptDirty` compara. Se setea SIEMPRE junto con TranscriptText y nunca por
        // separado -- ese es todo el truco (ver TranscriptDirtyState).
        if (value is { HasTranscript: true } && File.Exists(value.TranscriptPath))
            TranscriptText = _loadedTranscriptText = File.ReadAllText(value.TranscriptPath);
        else
            TranscriptText = _loadedTranscriptText = string.Empty;

        // Título/contexto son por-audio: se limpian al cambiar de selección.
        NoteTitle = string.Empty;
        NoteContext = string.Empty;
    }

    // ---- Abrir workspace ------------------------------------------------

    private bool CanOpen() => !IsBusy && !IsRecording;

    /// <summary>
    /// Único selector de carpeta de toda la app (antes había uno acá y otro en SyncWindow, cada
    /// uno con su propio setting): delega en <see cref="SyncCoordinator"/>, que fija la MISMA
    /// carpeta como workspace y como destino de sync.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void OpenWorkspace()
    {
        try
        {
            var path = SyncCoordinator.Instance.ChooseFolderAndStart();
            if (path is not null)
                LoadWorkspace(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir la carpeta: {ex.Message}";
        }
    }

    /// <summary>Abre la ventana de sincronización con la nube (login, carpeta y sync manual).</summary>
    [RelayCommand]
    private void OpenSyncWindow()
    {
        var window = new SyncWindow { Owner = Application.Current.MainWindow };
        window.Show();
    }

    /// <summary>Abre la ventana de configuración (cuenta, carpeta y comportamiento).</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        var window = new SettingsWindow { Owner = Application.Current.MainWindow };
        window.Show();
    }

    public void LoadWorkspace(string path)
    {
        _workspace = Workspace.OpenOrCreate(path);
        WorkspacePath = _workspace.RootPath;

        // La carpeta ya se persiste como SyncFolder desde SyncCoordinator (dueño único del
        // setting); acá solo se arma el árbol de proyectos sobre esa misma carpeta.
        StartWatching(_workspace.AudiosPath);
        OpenTranscriptsFolderCommand.NotifyCanExecuteChanged();
        RefreshAudios();
        StatusMessage = "Carpeta lista. Poné tus audios en la subcarpeta 'audios' o arrastralos acá.";
    }

    /// <summary>Vigila la carpeta 'audios' (y subcarpetas) y refresca el árbol solo al cambiar archivos.</summary>
    private void StartWatching(string audiosPath)
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(audiosPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.DirectoryName,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler onChange = (_, _) => ScheduleRefresh();
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, _) => ScheduleRefresh();
    }

    // Los eventos del watcher llegan en otro hilo: reprogramamos el refresh en el hilo de UI.
    private void ScheduleRefresh()
    {
        _refreshDebounce.Dispatcher.Invoke(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        });
    }

    /// <summary>
    /// Copia los archivos arrastrados al proyecto enfocado (o General). Si no hay workspace,
    /// crea uno por defecto en Documentos\AudioTranscriber.
    /// </summary>
    public void AddDroppedFiles(IEnumerable<string> paths)
    {
        try
        {
            if (_workspace is null)
            {
                // Sin carpeta configurada todavía: se crea la default y queda como LA carpeta
                // (workspace + sync), igual que si se hubiese elegido a mano.
                SyncCoordinator.Instance.SetFolder(DefaultWorkspacePath);
                LoadWorkspace(DefaultWorkspacePath);
            }

            var destFolder = SelectedProject?.Model.FolderPath ?? _workspace!.AudiosPath;

            int added = 0, ignored = 0;
            foreach (var src in paths)
            {
                if (!File.Exists(src))
                    continue;
                if (!Workspace.SupportedExtensions.Contains(Path.GetExtension(src)))
                {
                    ignored++;
                    continue;
                }
                File.Copy(src, Path.Combine(destFolder, Path.GetFileName(src)), overwrite: true);
                added++;
            }

            RefreshAudios();
            StatusMessage = ignored == 0
                ? $"Se agregaron {added} audio(s)."
                : $"Se agregaron {added} audio(s); {ignored} ignorado(s) por formato no soportado.";
        }
        catch (Exception ex)
        {
            // El texto crudo de la excepción NUNCA va a la UI (inglés, filtra rutas locales, y no
            // le dice a la usuaria qué hacer) — ver WorkspaceErrorFormatter.
            StatusMessage = WorkspaceErrorFormatter.FriendlyAddFilesError(ex);
            CrashLogger.Log(ex);
        }
    }

    /// <summary>
    /// "Cargar archivo(s)" (rediseño 2026-07-14): alternativa explícita al drag&amp;drop, con
    /// selector de Windows y multiselección -- paridad con el dropzone clickeable de la web
    /// (transcribe-workspace.tsx, "o hacé clic para elegirlos"). Reusa AddDroppedFiles tal cual,
    /// mismo destino/misma validación de formato que ya tenía el drag&amp;drop -- no es lógica
    /// nueva, es un segundo camino hacia la misma función.
    /// </summary>
    [RelayCommand]
    private void AddFiles()
    {
        var extensions = string.Join(";", Workspace.SupportedExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Elegí uno o más audios",
            Filter = $"Audio ({extensions})|{extensions}|Todos los archivos (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true)
            AddDroppedFiles(dialog.FileNames);
    }

    /// <summary>Reconstruye el árbol de proyectos, preservando la selección actual.</summary>
    [RelayCommand]
    private void RefreshAudios()
    {
        try
        {
            if (_workspace is null)
            {
                StatusMessage = "Primero elegí una carpeta de trabajo.";
                return;
            }

            var selProjName = SelectedProject?.Name;
            var selAudioPath = SelectedAudio?.FullPath;

            Projects.Clear();
            int total = 0;
            foreach (var proj in _workspace.ListProjects())
            {
                var pvm = new ProjectVm(proj);
                Projects.Add(pvm);
                total += pvm.Audios.Count;
            }

            // Antes de restaurar la selección: si el audio que se vuelve a seleccionar ya tiene
            // RemoteId resuelto, OpenNoteDetailCommand.CanExecute queda bien desde el primer render
            // (ver ResolveRemoteIds).
            ResolveRemoteIds();
            RestoreSelection(selProjName, selAudioPath);

            // "Unir notas" (ver región más abajo): cada AudioItemVm es una instancia NUEVA acá
            // (Projects.Clear() + reconstrucción completa), así que el checkbox de marcado siempre
            // arranca en false y hay que re-suscribirse para que MarkedForMergeCount seguía en
            // sync -- las instancias viejas quedan sin listener y se recolectan solas.
            foreach (var p in Projects)
                foreach (var a in p.Audios)
                    a.PropertyChanged += OnAudioPropertyChanged;
            OnPropertyChanged(nameof(MarkedForMergeCount));
            MergeSelectedCommand.NotifyCanExecuteChanged();
            // FEATURE 4 (2026-07-17): mismo motivo que MergeSelectedCommand arriba — cada
            // refresh reconstruye instancias nuevas de AudioItemVm, todas sin marcar.
            DeleteMarkedCommand.NotifyCanExecuteChanged();

            // "Resurfacing" (ver región más abajo): recalculado sobre el árbol recién reconstruido.
            ComputeResurfaceCandidate();

            // A.4 (DESIGN-REVIEW-2026-07-16.md): este ternario tenía dos ramas de naturaleza
            // distinta -- la vacía es onboarding (call-to-action), la no-vacía es inventario. Solo
            // la no-vacía migra a InventoryText, su propio canal en el footer; la vacía se queda en
            // StatusMessage. RefreshAudiosCommand (el botón ⟳) ya no da feedback aparte en
            // StatusMessage: InventoryText actualizándose ES el feedback.
            if (total == 0)
            {
                InventoryText = string.Empty;
                StatusMessage = "No hay audios todavía. Arrastralos acá o creá un proyecto.";
            }
            else
            {
                InventoryText = $"{total} audio(s) en {Projects.Count} proyecto(s).";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo leer los proyectos: {ex.Message}";
        }
    }

    private void RestoreSelection(string? projName, string? audioPath)
    {
        if (audioPath is not null)
        {
            foreach (var p in Projects)
                foreach (var a in p.Audios)
                    if (string.Equals(a.FullPath, audioPath, StringComparison.OrdinalIgnoreCase))
                    {
                        p.IsExpanded = true;
                        a.IsSelected = true;
                        return;
                    }
        }
        if (projName is not null)
        {
            var proj = Projects.FirstOrDefault(x => x.Name == projName);
            if (proj is not null)
                proj.IsSelected = true;
        }

        AutoSelectSingleAudio();
    }

    /// <summary>
    /// Con UN solo audio en todo el workspace y nada seleccionado, lo selecciona solo.
    ///
    /// Feedback de usuaria real (2026-07-15): "me hace seleccionarlo para recién habilitar
    /// transcribir, no es intuitivo". Tenía razón: arrastrás tu único audio, lo ves en pantalla, y
    /// "Transcribir" sigue apagado esperando un click que nada sugiere. Con un solo audio no hay
    /// ambigüedad posible sobre cuál querés — elegirlo por ella no le saca ninguna decisión.
    /// Con varios NO se auto-selecciona: ahí sí habría que adivinar, y adivinar mal es peor.
    /// </summary>
    private void AutoSelectSingleAudio()
    {
        if (SelectedAudio is not null)
            return;

        var all = Projects.SelectMany(p => p.Audios).ToList();
        if (all.Count != 1)
            return;

        var only = all[0];
        var owner = Projects.FirstOrDefault(p => p.Audios.Contains(only));
        if (owner is not null)
            owner.IsExpanded = true;
        only.IsSelected = true;
    }

    /// <summary>
    /// Resuelve <see cref="AudioItemVm.RemoteId"/> para cada audio Y <see cref="ProjectVm.RemoteId"/>
    /// para cada proyecto del árbol recién construido, leyendo el índice local de sync
    /// (<see cref="SyncIndex"/>, misma clase pública que ya usa <see cref="SyncCoordinator.BuildSyncEngine"/>
    /// -- no se toca nada de Core/Sync acá, solo se consume su API pública de solo lectura). Un item
    /// cuenta como "sincronizado" (RemoteId asignado) solo si su id aparece en la BASELINE del
    /// último sync exitoso -- no alcanza con que <see cref="LocalScanner"/> pueda calcularle un id
    /// determinístico, porque ese id todavía podría no existir en el servidor (nota/proyecto creado
    /// localmente, sync pendiente/offline/sin login). Best-effort: cualquier error (índice
    /// corrupto, DB bloqueada, workspace recién creado sin carpeta .synccache todavía) deja todos
    /// los RemoteId en null -- el botón "Abrir con IA"/"Asistente del proyecto" queda deshabilitado,
    /// nunca rompe el refresh del árbol.
    /// </summary>
    private void ResolveRemoteIds()
    {
        if (_workspace is null)
            return;
        try
        {
            var index = new SyncIndex(SyncIndex.DefaultPathFor(_workspace.RootPath));
            var idOverrides = index.LoadIdMap();
            var baseline = index.LoadBaseline();
            var snapshot = new LocalScanner().ScanDetailed(_workspace.RootPath, idOverrides);

            var byTranscriptPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot.Transcriptions.Values)
            {
                if (baseline.ContainsKey(entry.Id))
                    byTranscriptPath[entry.TranscriptPath] = entry.Id;
            }

            // Mismo criterio que arriba (byTranscriptPath), pero para proyectos: solo cuenta como
            // "sincronizado" si el id calculado por LocalScanner aparece en la baseline del último
            // sync exitoso. FolderPath es la clave estable para calzar snapshot.Projects (indexado
            // por id, no por carpeta) con cada ProjectVm -- LocalProjectEntry.FolderPath y
            // ProjectVm.Model.FolderPath vienen del mismo Workspace.ListProjects(), así que son
            // literalmente el mismo string.
            var byProjectFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in snapshot.Projects.Values)
            {
                if (baseline.ContainsKey(entry.Id))
                    byProjectFolder[entry.FolderPath] = entry.Id;
            }

            foreach (var p in Projects)
            {
                foreach (var a in p.Audios)
                    a.RemoteId = byTranscriptPath.TryGetValue(a.TranscriptPath, out var trId) ? trId : null;
                p.RemoteId = byProjectFolder.TryGetValue(p.Model.FolderPath, out var prId) ? prId : null;
            }
        }
        catch
        {
            // Best-effort a propósito (ver comentario del método): no debe tirar abajo RefreshAudios.
        }
    }

    private bool CanOpenNoteDetail() => SelectedAudio is { IsSyncedRemotely: true };

    /// <summary>
    /// "Asistente IA": abre <see cref="NoteDetailWindow"/>, la vista de detalle NATIVA de la nota
    /// seleccionada (resumen y formatos con IA -- ver el brief "Híbrido nativo" 2026-07-13).
    /// Reemplaza al viejo botón "Abrir con IA" (WebView2 embebido, eliminado en el mismo cambio):
    /// la ventana consume el mismo backend (<c>/api/summarize</c>, <c>/api/recipes*</c>) con el
    /// Bearer del sync, pero renderiza controles WPF reales, sin navegador embebido. Deshabilitado
    /// (ver <see cref="CanOpenNoteDetail"/>) hasta que la nota tenga un
    /// <see cref="AudioItemVm.RemoteId"/> confirmado (necesita el id remoto para pedirle el
    /// resumen/formato al backend).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenNoteDetail))]
    private void OpenNoteDetail()
    {
        if (SelectedAudio?.RemoteId is not { } remoteId || string.IsNullOrEmpty(remoteId))
            return;
        try
        {
            var title = Path.GetFileNameWithoutExtension(SelectedAudio.FileName);
            var window = new NoteDetailWindow(remoteId, title, TranscriptText)
            {
                Owner = Application.Current.MainWindow
            };
            window.Show();
            StatusMessage = "Abriendo el detalle de la nota…";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir el detalle: {ex.Message}";
        }
    }

    // ---- Helpers compartidos de IA (Búsqueda / Segundo cerebro / Unir notas) --------------------
    // Mismo criterio "helpers REPLICADOS, no heredados" que FormatosViewModel/NoteDetailViewModel
    // documentan explícitamente (ver comentario de clase de FormatosViewModel).

    private static async Task<string> GetAiAccessTokenOrThrowAsync(string action)
    {
        var token = await SyncCoordinator.Instance.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException($"Iniciá sesión desde 'Sincronización' para {action}.");
        return token;
    }

    private static string FriendlyAiErrorMessage(Exception ex) => ex switch
    {
        AiAssistException or InvalidOperationException => ex.Message,
        HttpRequestException => "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.",
        _ => $"Ocurrió un error inesperado: {ex.Message}",
    };

    // ---- Búsqueda (full-text sobre todas las notas, ver AiSearchClient) --------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSearchResults))]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSearchResults))]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSearchResults))]
    private string _searchError = string.Empty;

    public ObservableCollection<AiSearchResultVm> SearchResults { get; } = new();

    /// <summary>
    /// True cuando corresponde tapar el panel de transcripción con el panel de resultados (ver
    /// MainWindow.xaml): hay una búsqueda en curso, con error, o con resultados. Vacía la búsqueda
    /// (<see cref="ClearSearch"/>) y este panel se oculta solo.
    /// </summary>
    public bool ShowSearchResults =>
        !string.IsNullOrWhiteSpace(SearchQuery) && (IsSearching || SearchResults.Count > 0 || !string.IsNullOrEmpty(SearchError));

    private bool CanSearch() => !IsSearching && !string.IsNullOrWhiteSpace(SearchQuery);

    /// <summary>Cierra el panel de resultados (botón "✕ Cerrar").</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        SearchError = string.Empty;
        SearchResults.Clear();
    }

    /// <summary>Busca <see cref="SearchQuery"/> en todas las notas del usuario (ver <c>GET /api/notes/search</c>).</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        SearchError = string.Empty;
        IsSearching = true;
        try
        {
            var token = await GetAiAccessTokenOrThrowAsync("buscar en tus notas");
            var client = new AiSearchClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            var results = await client.SearchAsync(SearchQuery, token, CancellationToken.None);

            SearchResults.Clear();
            foreach (var r in results) SearchResults.Add(new AiSearchResultVm(r));
            if (results.Count == 0)
                SearchError = "No encontramos notas que coincidan con la búsqueda.";
        }
        catch (Exception ex)
        {
            SearchError = FriendlyAiErrorMessage(ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// Abre el resultado elegido: si la nota ya está sincronizada localmente (aparece en el árbol
    /// con el mismo <see cref="AudioItemVm.RemoteId"/>) usa SU transcript ya en disco (mismo detalle
    /// nativo que "🧠 Asistente IA"); si no, abre igual el detalle -- resumen/formatos/chat andan
    /// solo con el id remoto, el panel de transcripción queda vacío nomás.
    /// </summary>
    [RelayCommand]
    private void OpenSearchResult(AiSearchResultVm? result)
    {
        if (result is null)
            return;
        try
        {
            var local = FindAudioByRemoteId(result.Id);
            var title = local is not null ? Path.GetFileNameWithoutExtension(local.FileName) : result.Title;
            var transcriptText = local is { HasTranscript: true } && File.Exists(local.TranscriptPath)
                ? File.ReadAllText(local.TranscriptPath)
                : string.Empty;

            var window = new NoteDetailWindow(result.Id, title, transcriptText) { Owner = Application.Current.MainWindow };
            window.Show();

            if (local is not null)
                local.IsSelected = true;

            StatusMessage = "Abriendo el detalle de la nota…";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir la nota: {ex.Message}";
        }
    }

    private AudioItemVm? FindAudioByRemoteId(string remoteId)
    {
        foreach (var p in Projects)
            foreach (var a in p.Audios)
                if (string.Equals(a.RemoteId, remoteId, StringComparison.Ordinal))
                    return a;
        return null;
    }

    // ---- Segundo cerebro (chat sobre todas las notas, ver AiBrainClient / BrainWindow) -----------

    [RelayCommand]
    private void OpenBrainWindow()
    {
        var window = new BrainWindow { Owner = Application.Current.MainWindow };
        window.Show();
    }

    // ---- Asistente del proyecto (chat acotado a un proyecto + atajos + Combinar en documento) -----

    private bool CanOpenProjectAssistant() => SelectedProject is { IsGeneral: false };

    /// <summary>
    /// Abre <see cref="BrainWindow"/> acotada al proyecto seleccionado (ver
    /// <see cref="ChatScopeRouter.Project"/>), con los mismos atajos "Resumir"/"Próximos pasos" y
    /// "Combinar en documento" que ya tiene la web. Requiere que el proyecto ya tenga
    /// <see cref="ProjectVm.RemoteId"/> resuelto (ver <see cref="ResolveRemoteIds"/>): sin id
    /// remoto no hay forma de acotar el retrieval del backend a ESTE proyecto -- mismo criterio
    /// defensivo que <see cref="MergeSelected"/> con notas todavía no sincronizadas (avisa por
    /// <see cref="StatusMessage"/> y no abre la ventana, en vez de mandar un id inválido).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenProjectAssistant))]
    private void OpenProjectAssistant()
    {
        var project = SelectedProject;
        if (project is null)
            return;
        if (project.RemoteId is not { } remoteId || string.IsNullOrEmpty(remoteId))
        {
            StatusMessage = "Este proyecto todavía no terminó de sincronizarse. Esperá un momento y volvé a intentar.";
            return;
        }

        var mergeCandidates = project.Audios
            .Where(a => a.RemoteId is not null)
            .Take(AiMergeClient.MaxNoteCount)
            .Select(a => (RemoteId: a.RemoteId!, Title: Path.GetFileNameWithoutExtension(a.FileName)))
            .ToList();

        var window = new BrainWindow(remoteId, project.Title, mergeCandidates) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    // ---- Unir notas (modo explícito + AiMergeClient / MergeNotesWindow) ----------------------------

    /// <summary>
    /// Rediseño 2026-07-14: el checkbox de marcado (<see cref="AudioItemVm.IsMarkedForMerge"/>) ya
    /// NO está siempre visible al lado de cada audio -- ensuciaba la lista y no se entendía para
    /// qué servía. Ahora es un MODO explícito: arranca en false (lista limpia, sin checkboxes);
    /// el botón "Unir notas" lo prende (<see cref="ToggleMergeModeCommand"/>); "Cancelar" en la
    /// barra de instrucciones lo apaga y limpia la selección (<see cref="ClearMergeSelection"/>).
    /// </summary>
    [ObservableProperty]
    private bool _isMergeModeActive;

    /// <summary>Entra/sale del modo "Unir notas" (botón del header del explorador).</summary>
    [RelayCommand]
    private void ToggleMergeMode() => IsMergeModeActive = !IsMergeModeActive;

    /// <summary>Cuántos audios del árbol están marcados con <see cref="AudioItemVm.IsMarkedForMerge"/> ahora mismo.</summary>
    public int MarkedForMergeCount => Projects.SelectMany(p => p.Audios).Count(a => a.IsMarkedForMerge);

    /// <summary>
    /// Reenvía cambios de <see cref="AudioItemVm.IsMarkedForMerge"/> de CUALQUIER audio del árbol a
    /// <see cref="MarkedForMergeCount"/> (propiedad computada, no observable por sí sola) y al
    /// CanExecute de <see cref="MergeSelectedCommand"/>. Suscripto en <see cref="RefreshAudios"/>
    /// a cada <see cref="AudioItemVm"/> reconstruido.
    /// </summary>
    private void OnAudioPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AudioItemVm.IsMarkedForMerge))
            return;
        OnPropertyChanged(nameof(MarkedForMergeCount));
        MergeSelectedCommand.NotifyCanExecuteChanged();
        // FEATURE 4 (2026-07-17): "Borrar (N)" reusa el mismo marcado — mismo refresco de
        // CanExecute que MergeSelectedCommand, ver DeleteMarkedCommand más abajo.
        DeleteMarkedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Botón "Cancelar" de la barra de instrucciones del modo unir: desmarca todos los audios
    /// elegidos Y sale del modo (vuelve la lista limpia, sin checkboxes).
    /// </summary>
    [RelayCommand]
    private void ClearMergeSelection()
    {
        foreach (var a in Projects.SelectMany(p => p.Audios))
            a.IsMarkedForMerge = false;
        IsMergeModeActive = false;
    }

    private bool CanMergeSelected() => AiMergeClient.CanMergeNoteCount(MarkedForMergeCount);

    /// <summary>
    /// Abre <see cref="MergeNotesWindow"/> con los audios marcados. Solo pueden unirse notas ya
    /// sincronizadas (necesitan <see cref="AudioItemVm.RemoteId"/> -- el backend opera sobre ids
    /// remotos, mismo requisito que "🧠 Asistente IA"): si alguna marcada todavía no sincronizó, se
    /// avisa en vez de abrir la ventana con una lista incompleta.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMergeSelected))]
    private void MergeSelected()
    {
        var marked = Projects.SelectMany(p => p.Audios).Where(a => a.IsMarkedForMerge).ToList();
        if (marked.Any(a => a.RemoteId is null))
        {
            StatusMessage = "Alguna de las notas elegidas todavía no terminó de sincronizarse. Esperá un momento y volvé a intentar.";
            return;
        }

        var notes = marked.Select(a => (RemoteId: a.RemoteId!, Title: Path.GetFileNameWithoutExtension(a.FileName))).ToList();
        var window = new MergeNotesWindow(notes) { Owner = Application.Current.MainWindow };
        window.Show();

        // La ventana ya se llevó los ids que necesita (capturados arriba en "notes"): salir del
        // modo unir y limpiar el marcado local no le afecta, y deja la lista lista para la
        // próxima vez sin marcas viejas colgando.
        ClearMergeSelection();
    }

    private bool CanDeleteMarked() => MarkedForMergeCount > 0;

    /// <summary>
    /// "Borrar (N)" (FEATURE 4, 2026-07-17): reusa el mismo marcado de "Unir notas"
    /// (<see cref="AudioItemVm.IsMarkedForMerge"/>) para borrar TODOS los audios marcados de una,
    /// con confirmación. A diferencia de "Unir (N)", funciona con CUALQUIER audio marcado: no
    /// exige mínimo de 2 ni que estén sincronizados (el borrado no toca el backend, solo el disco
    /// local — ver <see cref="Workspace.DeleteAudios"/>). Mismo destino que un borrado individual
    /// (<see cref="DeleteAudio"/>): papelera, nunca permanente.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteMarked))]
    private void DeleteMarked()
    {
        if (_workspace is null)
            return;

        var marked = Projects.SelectMany(p => p.Audios).Where(a => a.IsMarkedForMerge).ToList();
        if (marked.Count == 0)
            return;

        if (!Confirm($"¿Borrar los {marked.Count} audios seleccionados? Van a la papelera."))
            return;

        try
        {
            // Resuelve el proyecto dueño de cada audio ANTES de borrar (mismo criterio que
            // DeleteAudio -- por membresía real en Projects, no por SelectedProject).
            var ownership = marked
                .Select(a => (Project: Projects.FirstOrDefault(p => p.Audios.Contains(a)), Audio: a))
                .Where(x => x.Project is not null)
                .Select(x => (Project: x.Project!.Model, Audio: x.Audio.Model))
                .ToList();

            _workspace.DeleteAudios(marked.Select(a => a.Model));

            // Bugfix 2026-07-21 (bug #1): mismo seam que DeleteAudio -- registra un tombstone de
            // sync por cada audio borrado, para que el próximo ciclo los pushee como borrados.
            SyncCoordinator.Instance.MarkAudiosDeletedForSync(ownership);
            SyncCoordinator.Instance.RequestSync();

            if (SelectedAudio is not null && marked.Contains(SelectedAudio))
                SelectedAudio = null;
            IsMergeModeActive = false;
            StatusMessage = $"Se borraron {marked.Count} audio(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo borrar: {ex.Message}";
        }
        finally
        {
            // Siempre, incluso ante un fallo a mitad de camino (algún audio sí se movió a
            // .papelera/ antes de que otro tirara): el árbol tiene que reflejar lo que
            // efectivamente quedó en disco, no lo que se INTENTÓ borrar.
            RefreshAudios();
        }
    }

    // ---- Resurfacing (nota vieja sugerida, 100% local -- ver ResurfaceCandidatePicker) -----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResurfaceCard))]
    private AudioItemVm? _resurfaceAudio;

    [ObservableProperty]
    private string _resurfaceText = string.Empty;

    public bool ShowResurfaceCard => ResurfaceAudio is not null;

    /// <summary>
    /// Recalcula qué nota (si alguna) sugerir en la card de resurfacing, sobre el árbol recién
    /// reconstruido (llamado desde <see cref="RefreshAudios"/>). Candidatas: audios CON transcript,
    /// con fecha local resuelta (<see cref="AudioItemVm.CreatedAtLocal"/>) y lo bastante viejos
    /// (<see cref="ResurfaceCandidatePicker.IsEligible"/>); se excluyen los ya descartados
    /// (<see cref="AppSettings.ResurfaceDismissedIds"/>, persistido -- ver <see cref="DismissResurface"/>).
    /// </summary>
    private void ComputeResurfaceCandidate()
    {
        var now = DateTime.Now;
        var byId = new Dictionary<string, AudioItemVm>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ResurfaceCandidate>();

        foreach (var p in Projects)
        {
            foreach (var a in p.Audios)
            {
                if (!a.HasTranscript || a.CreatedAtLocal is not { } createdAt)
                    continue;
                if (!ResurfaceCandidatePicker.IsEligible(createdAt, now))
                    continue;

                byId[a.TranscriptPath] = a;
                candidates.Add(new ResurfaceCandidate(a.TranscriptPath, Path.GetFileNameWithoutExtension(a.FileName), createdAt));
            }
        }

        var dismissed = new HashSet<string>(_settings.ResurfaceDismissedIds, StringComparer.OrdinalIgnoreCase);
        var picked = ResurfaceCandidatePicker.PickCandidate(candidates, dismissed);

        if (picked is null || !byId.TryGetValue(picked.Id, out var audio))
        {
            ResurfaceAudio = null;
            ResurfaceText = string.Empty;
            return;
        }

        ResurfaceAudio = audio;
        ResurfaceText = $"{ResurfaceCandidatePicker.FormatRelativeTime(picked.CreatedAt, now)} capturaste esto";
    }

    /// <summary>Abre la nota sugerida (la selecciona en el árbol, mismo camino que <see cref="SelectProjectFile"/>).</summary>
    [RelayCommand]
    private void OpenResurface()
    {
        if (ResurfaceAudio is not null)
            ResurfaceAudio.IsSelected = true;
    }

    /// <summary>Descarta la sugerencia actual (persistido -- no vuelve a aparecer en próximas sesiones).</summary>
    [RelayCommand]
    private void DismissResurface()
    {
        if (ResurfaceAudio is null)
            return;
        var id = ResurfaceAudio.TranscriptPath;
        if (!_settings.ResurfaceDismissedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            _settings.ResurfaceDismissedIds.Add(id);
            _settings.Save();
        }
        ComputeResurfaceCandidate();
    }

    // ---- Selección del árbol (llamado desde el code-behind) -------------

    /// <summary>Actualiza la selección según el nodo elegido en el TreeView.</summary>
    public void OnTreeSelectionChanged(object? selected)
    {
        // Antes de cambiar de audio, se guarda lo que haya sin guardar. Ver SaveDirtyTranscript.
        SaveDirtyTranscript();

        switch (selected)
        {
            case AudioItemVm audio:
                SelectedAudio = audio;
                SelectedProject = Projects.FirstOrDefault(p => p.Audios.Contains(audio)) ?? SelectedProject;
                break;
            case ProjectVm project:
                SelectedProject = project;
                SelectedAudio = null;
                break;
        }
    }

    /// <summary>
    /// Selecciona un audio elegido desde el listado de la vista de proyecto (panel derecho, ver
    /// <see cref="ShowProjectFilesView"/>). No toca SelectedAudio directo: marca IsSelected en el
    /// propio AudioItemVm (mismo mecanismo que <see cref="RestoreSelection"/> ya usa tras un
    /// refresh), lo que selecciona su TreeViewItem por el binding TwoWay y dispara
    /// OnTreeSelectionChanged con el audio — un solo camino para "seleccionar un audio", sin
    /// duplicar la lógica de arriba.
    /// </summary>
    public void SelectProjectFile(AudioItemVm? audio)
    {
        if (audio is not null)
            audio.IsSelected = true;
    }

    // ---- Operaciones del explorador -------------------------------------

    /// <summary>Crea un proyecto nuevo (pide el nombre por diálogo simple).</summary>
    [RelayCommand]
    private void NewProject()
    {
        if (_workspace is null)
            return;
        var name = PromptText("Nuevo proyecto", "Nombre del proyecto:", "Sin título");
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            _workspace.CreateProject(name);
            RefreshAudios();
            StatusMessage = $"Proyecto '{name}' creado.";
        }
        catch (Exception ex) { StatusMessage = $"No se pudo crear el proyecto: {ex.Message}"; }
    }

    private bool CanEditProject() => SelectedProject is { IsGeneral: false };

    /// <summary>
    /// Edita el proyecto entero: título, descripción y color. Único camino de escritura para
    /// metadata de proyecto -- usa <see cref="ProjectMetaEditor"/> para que, si el título cambia,
    /// la carpeta se renombre junto con él (ver esa clase para el porqué: antes había un botón
    /// separado que solo renombraba la carpeta y otro que solo pisaba el título, y se desalineaban
    /// entre sí).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private void EditProjectMeta()
    {
        if (_workspace is null || SelectedProject is null || SelectedProject.IsGeneral)
            return;
        var result = ProjectInfoDialog.Show(SelectedProject.Title, SelectedProject.Description, SelectedProject.Color);
        if (result is null)
            return;
        try
        {
            var outcome = ProjectMetaEditor.Apply(
                _workspace, SelectedProject.Model, result.Value.Title, result.Value.Description, result.Value.Color);
            if (!outcome.Success)
            {
                StatusMessage = outcome.ErrorMessage!;
                return;
            }

            RefreshAudios();
            // RefreshAudios reconstruye Projects desde disco (ProjectVm.Model es de solo lectura,
            // no se puede parchear in-place cuando la carpeta cambió de nombre) y restaura la
            // selección por el nombre de carpeta VIEJO, capturado antes del posible rename de
            // ProjectMetaEditor.Apply -- no lo va a encontrar si hubo rename. Re-seleccionar acá
            // por outcome.NewFolderName es lo que mantiene el proyecto recién editado enfocado en
            // el árbol.
            SelectedProject = Projects.FirstOrDefault(p => p.Name == outcome.NewFolderName) ?? SelectedProject;
            OnPropertyChanged(nameof(SelectedProjectDescription));
            StatusMessage = "Información del proyecto guardada.";
        }
        catch (Exception) { StatusMessage = "No se pudo guardar la información del proyecto."; }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private void DeleteProject()
    {
        if (_workspace is null || SelectedProject is null)
            return;
        // Title, no Name: es lo que el árbol le muestra a la usuaria (ver ProjectVm.Header). Antes
        // decía Name (nombre de carpeta) -- después de editar el título una vez, este cartel podía
        // nombrar un proyecto DISTINTO del que se ve seleccionado en pantalla.
        if (!Confirm($"¿Borrar el proyecto '{SelectedProject.Title}' con todos sus audios y transcripciones?"))
            return;
        try
        {
            _workspace.DeleteProject(SelectedProject.Model);
            SelectedProject = null;
            RefreshAudios();
        }
        catch (Exception ex) { StatusMessage = $"No se pudo borrar: {ex.Message}"; }
    }

    private bool CanEditAudio() => SelectedAudio is not null;

    [RelayCommand(CanExecute = nameof(CanEditAudio))]
    private void RenameAudio()
    {
        if (_workspace is null || SelectedAudio is null)
            return;
        var current = Path.GetFileNameWithoutExtension(SelectedAudio.FileName);
        var name = PromptText("Renombrar audio", "Nuevo nombre (sin extensión):", current);
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            _workspace.RenameAudio(SelectedAudio.Model, name);
            RefreshAudios();
        }
        catch (Exception ex) { StatusMessage = $"No se pudo renombrar: {ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanEditAudio))]
    private void DeleteAudio()
    {
        if (_workspace is null || SelectedAudio is null)
            return;
        if (!Confirm($"¿Borrar '{SelectedAudio.FileName}' y su transcripción?"))
            return;
        try
        {
            // Resuelto por membresía real (no SelectedProject a secas: mismo criterio que
            // OnTreeSelectionChanged) para no depender de que la selección de proyecto esté
            // sincronizada con la del audio en este punto exacto.
            var owner = Projects.FirstOrDefault(p => p.Audios.Contains(SelectedAudio));
            var audio = SelectedAudio.Model;

            _workspace.DeleteAudio(audio);

            // Bugfix 2026-07-21 (bug #1): registra el tombstone de sync DESPUÉS del borrado local
            // ya aplicado -- ver SyncCoordinator.MarkAudioDeletedForSync. Sin esto, la ausencia
            // local nunca se propagaba a la nube (ver MergeWithLocalTombstones en Core). Defensivo
            // si el owner no se resolvió (no debería pasar en el flujo normal de la UI).
            if (owner is not null)
            {
                SyncCoordinator.Instance.MarkAudioDeletedForSync(owner.Model, audio);
                SyncCoordinator.Instance.RequestSync();
            }

            SelectedAudio = null;
            RefreshAudios();
        }
        catch (Exception ex) { StatusMessage = $"No se pudo borrar: {ex.Message}"; }
    }

    /// <summary>Mueve un audio a otro proyecto (usado por el menú y el drag&amp;drop).</summary>
    public void MoveAudioToProject(AudioItemVm audio, ProjectVm target)
    {
        if (_workspace is null)
            return;
        try
        {
            _workspace.MoveAudio(audio.Model, target.Model);
            RefreshAudios();
            StatusMessage = $"'{audio.FileName}' movido a {target.Title}.";
        }
        catch (Exception ex) { StatusMessage = $"No se pudo mover: {ex.Message}"; }
    }

    /// <summary>Mueve el audio seleccionado al proyecto elegido en el submenú "Mover a".</summary>
    [RelayCommand]
    private void MoveToProject(ProjectVm? target)
    {
        if (SelectedAudio is not null && target is not null)
            MoveAudioToProject(SelectedAudio, target);
    }

    private bool CanEditSelection() => SelectedAudio is not null || SelectedProject is { IsGeneral: false };

    /// <summary>
    /// Edita lo que esté seleccionado: si es un audio, lo renombra (como siempre). Si es un
    /// proyecto, abre el editor completo (título/descripción/color) -- ya no hay un botón aparte
    /// que solo renombraba la carpeta del proyecto sin tocar su título, ver
    /// <see cref="ProjectMetaEditor"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void RenameSelected()
    {
        if (SelectedAudio is not null) RenameAudio();
        else if (SelectedProject is { IsGeneral: false }) EditProjectMeta();
    }

    /// <summary>Borra lo que esté seleccionado (audio o proyecto).</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void DeleteSelected()
    {
        if (SelectedAudio is not null) DeleteAudio();
        else if (SelectedProject is { IsGeneral: false }) DeleteProject();
    }

    // ---- Transcribir ----------------------------------------------------

    // FEATURE 2 (2026-07-17): con un PROYECTO seleccionado (sin audio puntual) y al menos un
    // audio sin transcribir, "Transcribir" pasa a transcribir el proyecto ENTERO, uno por uno —
    // ver TranscribeProjectAsync más abajo. Mira los mismos AudioItemVm.HasAudio/HasTranscript
    // que ya usa el árbol, vía BatchTranscribePlanner (Core, testeable sin UI).
    private static bool HasPendingProjectAudios(ProjectVm project) =>
        project.Audios.Any(a => BatchTranscribePlanner.IsPending(new BatchTranscribeAudioStatus(a.HasAudio, a.HasTranscript)));

    // SelectedAudio is { HasAudio: true } (en vez de "is not null"): una transcripción SOLO TEXTO
    // (ver AudioItemVm.HasAudio) ya tiene su texto -- no hay archivo de audio real que transcribir.
    // (IsGroq || IsLocalModelAvailable): con el motor Local, el botón queda deshabilitado hasta
    // que el modelo ya esté descargado -- ver la región "Descarga del modelo local" más arriba
    // para el diagnóstico completo del bug que esto resuelve. La rama de proyecto (SelectedAudio
    // null + SelectedProject con pendientes) es FEATURE 2 -- ver HasPendingProjectAudios arriba y
    // TranscribeProjectAsync más abajo. _isBatchTranscribing bloquea un segundo click mientras un
    // lote ya está corriendo (IsBusy solo, un solo Audio del lote, no del lote completo).
    private bool CanTranscribe() =>
        !IsBusy && !IsRecording && !_isBatchTranscribing && (IsGroq || IsLocalModelAvailable) &&
        (SelectedAudio is { HasAudio: true }
            || (SelectedAudio is null && SelectedProject is not null && HasPendingProjectAudios(SelectedProject)));

    /// <summary>
    /// Motivo REAL por el que "Transcribir" está apagado (o null si está prendido). Espeja
    /// exactamente las condiciones de <see cref="CanTranscribe"/>, priorizando el bloqueo más
    /// barato de resolver — ver TranscribeGateFormatter para el bug que esto arregla (el tooltip
    /// era un string fijo que culpaba al modelo local aunque el bloqueo real fuese otro, y mandó a
    /// una usuaria a descargar 1,5 GB al pedo). <c>hasPendingProjectAudios</c> (FEATURE 2) evita
    /// que este tooltip diga "Elegí un audio" con un proyecto batch-transcribible ya elegido.
    /// </summary>
    public string? TranscribeDisabledReason => TranscribeGateFormatter.DisabledReason(
        IsBusy || _isBatchTranscribing, IsRecording, SelectedAudio is { HasAudio: true }, IsGroq, IsLocalModelAvailable,
        hasPendingProjectAudios: SelectedAudio is null && SelectedProject is not null && HasPendingProjectAudios(SelectedProject));

    /// <summary>
    /// Punto de entrada de "Transcribir" (botón/comando): despacha a transcripción de UN audio
    /// (comportamiento de siempre, <see cref="TranscribeSelectedAudioAsync"/>) o de un PROYECTO
    /// entero (FEATURE 2, 2026-07-17, <see cref="TranscribeProjectAsync"/>) según qué haya
    /// seleccionado — ver <see cref="CanTranscribe"/> arriba para las condiciones exactas de cada
    /// rama. Sigue siendo el método que invoca <c>StopRecordingAsync</c> directo tras grabar
    /// (SelectedAudio siempre no-null en ese camino, así que siempre cae en la rama de un audio).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task TranscribeAsync()
    {
        if (SelectedAudio is null && SelectedProject is not null)
        {
            await TranscribeProjectAsync(SelectedProject);
            return;
        }

        await TranscribeSelectedAudioAsync();
    }

    /// <summary>
    /// Transcribe todos los audios sin transcribir de <paramref name="project"/>, uno por uno
    /// (FEATURE 2, 2026-07-17, brief 1.0.52). Pide confirmación antes de arrancar y reusa el MISMO
    /// camino que la transcripción de un solo audio (<see cref="TranscribeSelectedAudioAsync"/>),
    /// seteando <see cref="SelectedAudio"/> y esperando cada llamada de a una -- TranscribeAsync
    /// coordina con SyncCoordinator (zona sensible) y loopear await secuencial es lo seguro, sin
    /// tocar nada de su lógica interna. Si un audio falla sigue con el resto y avisa al final
    /// cuántos salieron mal (ver <see cref="BatchTranscribePlanner.SummaryMessage"/>).
    ///
    /// Riesgo conocido y aceptado: cada iteración pasa por <see cref="SelectedAudio"/> (la misma
    /// propiedad que toca un click del usuario en el árbol), así que seleccionar OTRO nodo a mano
    /// mientras el lote corre puede pisar la selección de la iteración en curso. Es el trade-off
    /// que el propio brief pide ("reusar el mismo camino... es lo seguro") a cambio de no tocar
    /// TranscribeSelectedAudioAsync/SyncCoordinator.
    /// </summary>
    private async Task TranscribeProjectAsync(ProjectVm project)
    {
        if (_workspace is null)
            return;

        // Mismo predicado que HasPendingProjectAudios (arriba, usado por CanTranscribe): una sola
        // fuente de verdad de "qué cuenta como pendiente" en vez de repetir la condición a mano.
        var pending = project.Audios
            .Where(a => BatchTranscribePlanner.IsPending(new BatchTranscribeAudioStatus(a.HasAudio, a.HasTranscript)))
            .ToList();
        if (pending.Count == 0)
            return; // CanTranscribe ya lo filtra; defensa en profundidad.

        if (!Confirm(BatchTranscribePlanner.ConfirmMessage(project.Title, pending.Count)))
            return;

        _batchCancelRequested = false;
        _isBatchTranscribing = true;
        TranscribeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TranscribeDisabledReason));

        int attempted = 0, failures = 0;
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                var audio = pending[i];
                if (audio.HasTranscript)
                    continue; // ya se transcribió por otro medio mientras esperaba su turno.

                attempted++;
                StatusMessage = BatchTranscribePlanner.ProgressMessage(i + 1, pending.Count);
                SelectedAudio = audio;
                await TranscribeSelectedAudioAsync();

                if (!audio.HasTranscript)
                    failures++;

                if (_batchCancelRequested)
                    break;
            }
        }
        finally
        {
            SelectedAudio = null; // vuelve a la vista de proyecto (ShowProjectFilesView).
            _isBatchTranscribing = false;
            TranscribeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(TranscribeDisabledReason));
        }

        StatusMessage = _batchCancelRequested
            ? BatchTranscribePlanner.CancelledMessage(attempted, pending.Count, failures)
            : BatchTranscribePlanner.SummaryMessage(pending.Count, failures);
    }

    /// <summary>Transcribe UN audio: <see cref="SelectedAudio"/> (comportamiento de siempre, sin cambios de lógica — ver FEATURE 2 arriba para el camino de lote).</summary>
    private async Task TranscribeSelectedAudioAsync()
    {
        if (_workspace is null || SelectedAudio is null)
            return;
        if (!SelectedAudio.HasAudio)
        {
            // Defensa en profundidad: CanTranscribe ya lo filtra, pero por si se invoca el comando
            // programáticamente sin pasar por el CanExecute.
            StatusMessage = "Esta transcripción no tiene audio (se creó como solo texto).";
            return;
        }
        if (!IsGroq && !IsLocalModelAvailable)
        {
            // Defensa REAL (no solo en profundidad): a diferencia de los otros dos checks de acá
            // arriba, este SÍ hace falta -- StopRecordingAsync llama a TranscribeAsync() directo
            // (no a través de TranscribeCommand), así que CanTranscribe NO se evalúa en ese
            // camino. Sin este guard, grabar con el motor Local sin el modelo descargado y aceptar
            // "¿Transcribirla ahora?" volvería a disparar la descarga escondida adentro de la
            // transcripción -- exactamente el bug reportado.
            StatusMessage = "El modelo local todavía no está descargado. Descargalo con el botón \"Descargar modelo\" antes de transcribir.";
            return;
        }

        // El motor nube corta en 25 MB: si el archivo no entra, se decide ACÁ y no después de
        // subirlo entero para cosechar un 413 (un usuario real trajo un video de 1,6 GB — 65x el
        // tope — que habría significado una subida larguísima para un error garantizado).
        // Nunca en silencio: el motor lo eligió la usuaria a propósito.
        var sizeDecision = EngineSelector.Decide(SelectedAudio.SizeBytes, IsGroq, IsLocalModelAvailable);
        if (sizeDecision == EngineDecision.NeedsLocalModel)
        {
            StatusMessage = EngineSelector.Notice(sizeDecision, SelectedAudio.SizeBytes)!;
            return;
        }
        if (sizeDecision == EngineDecision.SwitchToLocal)
        {
            Engine = "local";
            StatusMessage = EngineSelector.Notice(sizeDecision, SelectedAudio.SizeBytes)!;
        }

        var audio = SelectedAudio;
        IsBusy = true;
        TranscriptText = string.Empty;
        TranscribePercent = 0;
        LogLines.Clear();
        _cts = new CancellationTokenSource();

        // Cronómetro en vivo: arranca ahora, se refresca cada segundo.
        _elapsedSw.Restart();
        ElapsedText = "00:00";
        _elapsedTimer.Start();

        // Registro de actividad con hora. Marshalea al hilo de UI.
        var log = new Progress<string>(line =>
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}"));

        // Dato de entrada: nombre y peso del audio.
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] Audio: {audio.FileName} — {audio.SizeText}");

        // Reporta segmentos en streaming; Progress marshalea al hilo de UI.
        // El primer segmento marca que la descarga/carga terminó y ya está transcribiendo.
        // segments (con los tiempos de cada uno) se guarda además de sb (el texto plano): solo
        // hace falta para identificar quién habla (SpeakerAssigner.Assign, ver más abajo), pero
        // juntarlo acá es gratis -- no hay que volver a transcribir para tenerlo.
        var sb = new StringBuilder();
        var segments = new List<TranscriptSegment>();
        var progress = new Progress<TranscriptSegment>(seg =>
        {
            if (sb.Length == 0)
                IsDownloading = false;
            sb.Append(seg.Text);
            TranscriptText = sb.ToString();
            segments.Add(seg);
        });

        // Progreso real de la transcripción (0–100), según cuánto del audio se procesó.
        var transcribeProgress = new Progress<double>(pct =>
        {
            TranscribePercent = pct;
            StatusMessage = $"Transcribiendo '{audio.FileName}'… {pct:0}%";
        });

        // Progreso de descarga del modelo (barra real con %). Marshalea al hilo de UI.
        var downloadProgress = new Progress<ModelDownloadProgress>(p =>
        {
            IsDownloading = true;
            DownloadPercent = p.Percent;
            StatusMessage = p.TotalBytes > 0
                ? $"Descargando modelo (única vez): {p.MegabytesReceived:0} / {p.TotalMegabytes:0} MB ({p.Percent:0}%)"
                : $"Descargando modelo (única vez): {p.MegabytesReceived:0} MB…";
        });

        try
        {
            string text;
            if (IsGroq)
            {
                // ---- Motor nube: transcribe vía backend (la key de Groq vive server-side, ya no
                // en esta PC -- ver CloudTranscriptionService). Reusa el mismo access token y el
                // mismo HttpClient que ya usa SyncCoordinator para el sync automático. ----
                var accessToken = await SyncCoordinator.Instance.GetValidAccessTokenAsync();
                if (string.IsNullOrEmpty(accessToken))
                    throw new InvalidOperationException(
                        "Iniciá sesión desde 'Sincronización' para transcribir en la nube.");

                StatusMessage = IsTranslateMode
                    ? $"Transcribiendo y traduciendo '{audio.FileName}'…"
                    : $"Transcribiendo '{audio.FileName}' en la nube…";
                var cloud = new CloudTranscriptionService(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
                text = await cloud.TranscribeAsync(
                    audio.FullPath, accessToken, GroqModel, log, _cts.Token,
                    translate: IsTranslateMode, targetLanguage: TranslationTargetLanguage);
                TranscriptText = text;
            }
            else
            {
                // ---- Motor Local (Whisper en tu PC) ----
                // Reusa _modelProvider (mismo modelo/misma carpeta que ya chequeó el guard de
                // arriba y que usa DownloadModelCommand) en vez de crear un WhisperModelProvider
                // nuevo -- un solo punto de verdad sobre "¿está el modelo?".
                _service ??= new TranscriptionService(_modelProvider);
                _service.Language = Language;

                // _modelProvider.ModelPath (no un literal "ggml-small-...bin" hardcodeado): ese
                // hardcode asumía que el modelo local SIEMPRE era Small, algo que dejó de ser
                // cierto con el selector de modelo (2026-07-15, ver LocalModelId) -- con otro
                // modelo elegido, el literal viejo nunca existía y este mensaje jamás se mostraba.
                if (File.Exists(_modelProvider.ModelPath))
                    StatusMessage = $"Preparando '{audio.FileName}' (convirtiendo audio y cargando modelo)…";

                // keepConvertedWav: si vamos a identificar hablantes, sherpa-onnx necesita ESE
                // MISMO WAV (16 kHz mono) que TranscriptionService ya convierte para Whisper -- se
                // lo pedimos conservar en vez de convertir el audio una segunda vez. Se borra en
                // el finally de más abajo, se haya podido diarizar o no.
                text = await Task.Run(
                    () => _service.TranscribeAsync(
                        audio.FullPath, progress, _cts.Token, downloadProgress, transcribeProgress, log,
                        keepConvertedWav: UseDiarization));

                IsDownloading = false;

                if (UseDiarization)
                    text = await TryLabelSpeakersAsync(text, segments, log, _cts.Token);
            }

            // Guardar el .txt en /transcripts. Se usa audio.TranscriptPath (ya calculado por
            // Workspace.ListAudiosIn con el projectFolder correcto) en vez de recalcularlo acá
            // sin proyecto: recalcularlo sin projectFolder mandaba SIEMPRE el .txt a la raíz de
            // transcripts/ aunque el audio perteneciera a un proyecto (mismo criterio que usa el
            // comando "Guardar", ver SaveTranscript() más abajo).
            var outPath = audio.TranscriptPath;
            _workspace.SaveTranscript(outPath, text);

            TranscriptText = text;
            audio.HasTranscript = true;
            StatusMessage = $"Listo. Transcript guardado en {outPath}";

            // Exportación automática a Obsidian/Drive (carpeta) si hay destino configurado.
            var mdPath = ExportToDestinations(audio, text);
            if (mdPath is not null)
                StatusMessage = $"Listo. Nota .md exportada a {mdPath}";

            SystemSounds.Asterisk.Play(); // aviso de que terminó
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Transcripción cancelada.";
        }
        catch (Exception ex)
        {
            // A.7 (DESIGN-REVIEW-2026-07-16.md): este era el ÚNICO de ~24 sitios de error de la app
            // que mandaba ex.Message crudo a StatusMessage (inglés, rutas locales expuestas con el
            // motor Local) y ni siquiera quedaba registrado en el log de crashes. Mismo molde que
            // WorkspaceErrorFormatter, ya usado en AddDroppedFiles/StartRecording más arriba.
            StatusMessage = TranscribeErrorFormatter.Friendly(ex);
            CrashLogger.Log(ex);
        }
        finally
        {
            _elapsedTimer.Stop();
            _elapsedSw.Stop();
            ElapsedText = _elapsedSw.Elapsed.ToString(@"mm\:ss");
            IsBusy = false;
            IsDownloading = false;
            DownloadPercent = 0;
            TranscribePercent = 0;
            _cts?.Dispose();
            _cts = null;

            // Por si quedó un WAV conservado para diarización (keepConvertedWav): sin importar si
            // se llegó a usar, se pudo identificar hablantes, o ni siquiera se intentó (excepción
            // antes de llegar ahí), ACÁ es el único lugar que garantiza limpiarlo siempre. No tira
            // si no hay nada que borrar.
            _service?.DeleteLastConvertedWav();
        }
    }

    /// <summary>
    /// Corre la identificación de hablantes sobre el WAV que <see cref="_service"/> acaba de
    /// convertir (ver <c>keepConvertedWav</c> más arriba) y devuelve el texto con "Persona 1:"/
    /// "Persona 2:"…. Si algo falla -- modelos no descargados, error del motor, lo que sea --
    /// NUNCA tira: devuelve <paramref name="plainText"/> tal cual y deja un aviso en LogLines/
    /// StatusMessage, porque perder una transcripción entera por un problema en un paso OPCIONAL
    /// sería peor que el problema original.
    /// </summary>
    private async Task<string> TryLabelSpeakersAsync(
        string plainText, IReadOnlyList<TranscriptSegment> segments, IProgress<string> log, CancellationToken ct)
    {
        try
        {
            var wavPath = _service?.LastConvertedWavPath
                ?? throw new InvalidOperationException("No se conservó el audio convertido para analizarlo.");

            log.Report("Identificando quién habla (puede tardar)…");
            StatusMessage = "Identificando quién habla…";

            var speakerSegments = await _diarizationService.DiarizeAsync(wavPath, log, ct);
            var labeled = SpeakerAssigner.Assign(segments, speakerSegments);

            if (SpeakerAssigner.DistinctSpeakers(labeled) < 2)
            {
                // No anunciamos "Persona 1/Persona 2" si en los hechos no se llegó a distinguir
                // más de una persona (ver el comentario de DistinctSpeakers en Core): la
                // transcripción sin marcar es más honesta que una etiqueta que no suma nada.
                log.Report("No se llegaron a distinguir dos o más personas; se deja la transcripción sin marcar.");
                return plainText;
            }

            return SpeakerTranscriptFormatter.Format(labeled);
        }
        catch (OperationCanceledException)
        {
            throw; // Cancelación real del usuario: la maneja el catch de TranscribeAsync.
        }
        catch (Exception ex)
        {
            log.Report($"No se pudo identificar quién habla ({ex.Message}). Transcripción sin marcar.");
            StatusMessage = "Transcripción lista, pero no se pudo identificar quién habla.";
            return plainText;
        }
    }

    private bool CanCancel() => IsBusy;

    /// <summary>
    /// Cancela lo que esté corriendo. <see cref="_batchCancelRequested"/> (FEATURE 2, 2026-07-17)
    /// solo importa mientras hay un lote de proyecto corriendo (ver TranscribeProjectAsync) — en
    /// una transcripción de un solo audio queda prendido sin que nadie lo lea, y el próximo lote
    /// lo reinicia a false apenas arranca.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _batchCancelRequested = true;
        _cts?.Cancel();
    }

    // ---- Grabar audio desde el micrófono ---------------------------------

    /// <summary>True mientras se está grabando (micrófono solo, o reunión -- ver <see cref="IsMeetingRecording"/>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranscribeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkspaceCommand))]
    [NotifyPropertyChangedFor(nameof(TranscribeDisabledReason))]
    [NotifyPropertyChangedFor(nameof(ShowStopMicButton))]
    [NotifyPropertyChangedFor(nameof(ShowStopMeetingButton))]
    private bool _isRecording;

    /// <summary>
    /// True cuando la grabación en curso es de una REUNIÓN (micrófono + audio del sistema), no
    /// una grabación normal de solo micrófono. Decide cuál de los dos botones "Detener grabación"
    /// se muestra (<see cref="ShowStopMicButton"/>/<see cref="ShowStopMeetingButton"/>) y a cuál de
    /// los dos grabadores le llega el "Detener" -- ver <see cref="StopRecordingAsync"/>. El
    /// cronómetro/VU meter (<see cref="RecordingElapsedText"/>/<see cref="RecordingLevel"/>) son
    /// los MISMOS para las dos, no hace falta duplicarlos.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStopMicButton))]
    [NotifyPropertyChangedFor(nameof(ShowStopMeetingButton))]
    private bool _isMeetingRecording;

    /// <summary>Tiempo transcurrido de la grabación en curso ("mm:ss").</summary>
    [ObservableProperty]
    private string _recordingElapsedText = "00:00";

    /// <summary>Nivel de audio en vivo del micrófono (0.0–1.0), para el VU meter simple.</summary>
    [ObservableProperty]
    private double _recordingLevel;

    /// <summary>True para mostrar "Detener grabación" cuando lo que está en curso es la grabación normal (solo micrófono).</summary>
    public bool ShowStopMicButton => IsRecording && !IsMeetingRecording;

    /// <summary>True para mostrar "Detener grabación" cuando lo que está en curso es la grabación de reunión.</summary>
    public bool ShowStopMeetingButton => IsRecording && IsMeetingRecording;

    private bool CanToggleRecording() => !IsBusy;

    /// <summary>Arranca o detiene la grabación normal (solo micrófono), según el estado actual.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private async Task ToggleRecording()
    {
        if (IsRecording)
            await StopRecordingAsync();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        try
        {
            if (_workspace is null)
            {
                // Igual criterio que AddDroppedFiles: sin carpeta configurada todavía, se crea la
                // default y queda como LA carpeta (workspace + sync).
                SyncCoordinator.Instance.SetFolder(DefaultWorkspacePath);
                LoadWorkspace(DefaultWorkspacePath);
            }

            var destFolder = SelectedProject?.Model.FolderPath ?? _workspace!.AudiosPath;
            var fileName = RecordingFileNamer.Generate(DateTime.Now);
            var path = Path.Combine(destFolder, fileName);

            _recorder ??= new MicrophoneRecorder();
            _recorder.LevelChanged += OnMicLevelReported;
            _recorder.CaptureError += OnRecordingCaptureError;
            _recorder.Start(path);

            _recordingSw.Restart();
            RecordingElapsedText = "00:00";
            _recordingTimer.Start();
            IsRecording = true;
            IsMeetingRecording = false;
            // A.5 (DESIGN-REVIEW-2026-07-16.md): antes decía solo "Grabando…", sin aclarar QUÉ se
            // graba -- a diferencia de StartMeetingRecording, que sí lo explicita. Con dos botones
            // de grabar que se distinguen por su label (ver el Button de arriba), el StatusMessage
            // tiene que quedar alineado con esa misma distinción.
            StatusMessage = "Grabando solo tu voz… presioná Detener cuando termines.";
        }
        catch (Exception ex)
        {
            // Mismo criterio que AddDroppedFiles: nunca el texto crudo de la excepción.
            StatusMessage = WorkspaceErrorFormatter.FriendlyRecordingError(ex);
            CrashLogger.Log(ex);
        }
    }

    // ---- Grabar reunión (micrófono + audio del sistema) -------------------------------------
    //
    // Reusa el mismo flujo que "Grabar audio" de arriba (mismo IsRecording/cronómetro/VU meter/
    // StopRecordingAsync -- ver comentario de IsMeetingRecording): lo único que cambia es qué
    // clase de Core arranca (MeetingRecorder en vez de MicrophoneRecorder) y el mensaje de estado,
    // porque MeetingRecorder captura DOS fuentes (ver su comentario de clase en Core/Audio) y
    // puede terminar grabando solo el micrófono si el audio del sistema no se pudo capturar.

    /// <summary>Una entrada del selector "de dónde grabar" -- ver <see cref="MeetingAudioSourceOptions"/>.
    /// <c>ProcessId</c> null significa "todo el audio de la computadora" (el comportamiento de
    /// siempre); con un PID, se le pide a <see cref="MeetingRecorder"/> que capture SOLO esa
    /// aplicación.</summary>
    public sealed record MeetingAudioSourceOption(string Label, int? ProcessId);

    private static readonly MeetingAudioSourceOption AllSystemAudioOption = new("Todo el audio de la computadora", null);

    /// <summary>Opciones del selector de "Grabar reunión": "todo el audio de la computadora" +
    /// una por cada aplicación con sonido activo ahora mismo. Se refresca al abrir el combo (ver
    /// <see cref="RefreshMeetingAudioSourceOptions"/>) porque las aplicaciones que suenan cambian
    /// todo el tiempo.</summary>
    [ObservableProperty]
    private ObservableCollection<MeetingAudioSourceOption> _meetingAudioSourceOptions = new() { AllSystemAudioOption };

    /// <summary>Qué elegiste en el selector de arriba. Nunca queda en null: por defecto
    /// <see cref="AllSystemAudioOption"/> (todo el audio de la PC, el comportamiento de siempre).</summary>
    [ObservableProperty]
    private MeetingAudioSourceOption _selectedMeetingAudioSource = AllSystemAudioOption;

    private bool CanToggleMeetingRecording() => !IsBusy;

    /// <summary>Arranca o detiene la grabación de la reunión, según el estado actual.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleMeetingRecording))]
    private async Task ToggleMeetingRecording()
    {
        if (IsRecording)
            await StopRecordingAsync();
        else
            StartMeetingRecording();
    }

    // ---- Selector de aplicación (llamado desde el code-behind al abrir el combo) --------------

    /// <summary>Vuelve a listar qué aplicaciones tienen audio activo ahora mismo. Las apps
    /// aparecen y desaparecen todo el tiempo (una pestaña de Chrome deja de sonar, se cierra
    /// Spotify...), así que esto se llama cada vez que se abre el combo, no una sola vez al
    /// arrancar la app.</summary>
    public void RefreshMeetingAudioSourceOptions()
    {
        var previousSelection = SelectedMeetingAudioSource.ProcessId;
        var apps = AudioAppLister.List();

        MeetingAudioSourceOptions.Clear();
        MeetingAudioSourceOptions.Add(AllSystemAudioOption);
        if (apps.Count == 0)
        {
            MeetingAudioSourceOptions.Add(new MeetingAudioSourceOption("No hay ninguna aplicación sonando ahora mismo", null));
        }
        else
        {
            foreach (var app in apps)
                MeetingAudioSourceOptions.Add(new MeetingAudioSourceOption(app.DisplayName, app.ProcessId));
        }

        SelectedMeetingAudioSource = MeetingAudioSourceOptions.FirstOrDefault(o => o.ProcessId == previousSelection)
            ?? AllSystemAudioOption;
    }

    private void StartMeetingRecording()
    {
        try
        {
            if (_workspace is null)
            {
                SyncCoordinator.Instance.SetFolder(DefaultWorkspacePath);
                LoadWorkspace(DefaultWorkspacePath);
            }

            var destFolder = SelectedProject?.Model.FolderPath ?? _workspace!.AudiosPath;
            var fileName = RecordingFileNamer.GenerateForMeeting(DateTime.Now);
            var path = Path.Combine(destFolder, fileName);

            _meetingRecorder ??= new MeetingRecorder();
            _meetingRecorder.LevelChanged += OnMicLevelReported;
            _meetingRecorder.CaptureError += OnRecordingCaptureError;
            _meetingRecorder.SystemAudioUnavailable += OnSystemAudioUnavailable;
            _meetingRecorder.AppAudioUnavailable += OnAppAudioUnavailable;
            _meetingRecorder.Start(path, SelectedMeetingAudioSource.ProcessId);

            _recordingSw.Restart();
            RecordingElapsedText = "00:00";
            _recordingTimer.Start();
            IsRecording = true;
            IsMeetingRecording = true;
            StatusMessage = _meetingRecorder switch
            {
                { IsAppAudioCaptured: true } =>
                    $"Grabando el audio de \"{SelectedMeetingAudioSource.Label}\" junto con tu micrófono… presioná Detener cuando termines.",
                { IsSystemAudioCaptured: true } =>
                    "Grabando la reunión (el audio de la reunión + tu micrófono)… presioná Detener cuando termines.",
                _ =>
                    "No se pudo captar el audio de la reunión: seguimos grabando solo tu micrófono. Presioná Detener cuando termines.",
            };
        }
        catch (Exception ex)
        {
            StatusMessage = WorkspaceErrorFormatter.FriendlyRecordingError(ex);
            CrashLogger.Log(ex);
        }
    }

    // El audio del sistema puede fallar al arrancar o cortarse a mitad de reunión (ver
    // MeetingRecorder.SystemAudioUnavailable): en los dos casos la grabación sigue, solo avisamos.
    private void OnSystemAudioUnavailable(Exception ex)
        => _recordingTimer.Dispatcher.Invoke(() =>
            StatusMessage = "No se pudo captar el audio de la reunión: seguimos grabando solo tu micrófono.");

    // La aplicación elegida en el selector no se pudo capturar (ver MeetingRecorder.AppAudioUnavailable):
    // la grabación NO se corta, sigue con todo el audio de la PC -- avisamos con algo accionable
    // en vez de dejar que el usuario piense que grabó solo esa app cuando en realidad grabó todo.
    private void OnAppAudioUnavailable(Exception ex)
        => _recordingTimer.Dispatcher.Invoke(() =>
            StatusMessage = $"No se pudo grabar solo \"{SelectedMeetingAudioSource.Label}\": seguimos grabando todo el audio de tu computadora junto con tu micrófono.");

    // Los eventos del grabador llegan desde el hilo de captura de NAudio: hay que marshalear al
    // hilo de UI antes de tocar propiedades bindeadas (mismo criterio que StartWatching/OnChange).
    private void OnMicLevelReported(double level)
        => _recordingTimer.Dispatcher.Invoke(() => RecordingLevel = level);

    private void OnRecordingCaptureError(Exception ex)
        => _recordingTimer.Dispatcher.Invoke(() => StatusMessage = $"Error de grabación: {ex.Message}");

    /// <summary>
    /// Frena la grabación en curso, sea normal (solo micrófono) o de reunión -- el resto del
    /// camino (guardar, refrescar el árbol, elegir proyecto/título, ofrecer transcribir) es
    /// EXACTAMENTE el mismo para las dos, así que solo esta primera parte (cuál grabador frenar)
    /// se bifurca según <see cref="IsMeetingRecording"/>.
    /// </summary>
    private async Task StopRecordingAsync()
    {
        string? path;
        if (IsMeetingRecording)
        {
            if (_meetingRecorder is null)
                return;

            path = _meetingRecorder.OutputPath;
            try
            {
                _meetingRecorder.Stop();
            }
            finally
            {
                _meetingRecorder.LevelChanged -= OnMicLevelReported;
                _meetingRecorder.CaptureError -= OnRecordingCaptureError;
                _meetingRecorder.SystemAudioUnavailable -= OnSystemAudioUnavailable;
                _meetingRecorder.AppAudioUnavailable -= OnAppAudioUnavailable;
            }
        }
        else
        {
            if (_recorder is null)
                return;

            path = _recorder.OutputPath;
            try
            {
                _recorder.Stop();
            }
            finally
            {
                _recorder.LevelChanged -= OnMicLevelReported;
                _recorder.CaptureError -= OnRecordingCaptureError;
            }
        }

        _recordingTimer.Stop();
        _recordingSw.Stop();
        IsRecording = false;
        IsMeetingRecording = false;
        RecordingLevel = 0;

        RefreshAudios();

        if (path is null || !File.Exists(path) || _workspace is null)
            return;

        StatusMessage = $"Grabación guardada: {Path.GetFileName(path)}.";

        var newAudioVm = FindAudioByFullPath(path);
        if (newAudioVm is null)
            return;

        // ---- Elegir proyecto destino + título (paridad con la web) ----------------------
        // El audio ya quedó guardado con nombre automático en la carpeta del proyecto que estaba
        // enfocado al ARRANCAR a grabar; acá se ofrece confirmarlo o moverlo/renombrarlo antes de
        // seguir, reusando Workspace.MoveAudio/RenameAudio (mismos métodos que ya usa el árbol).
        var owningProject = Projects.FirstOrDefault(p => p.Audios.Contains(newAudioVm));
        var currentProjectName = owningProject is { IsGeneral: false } ? owningProject.Name : null;
        var projectNames = Projects.Where(p => !p.IsGeneral).Select(p => p.Name).ToList();
        var defaultTitle = RecordingSaveDefaults.DefaultTitle(newAudioVm.FileName);
        var defaultProject = RecordingSaveDefaults.ResolveDefaultProject(projectNames, currentProjectName);

        var choice = RecordingSaveDialog.Show(projectNames, defaultProject, defaultTitle);
        if (choice is { } picked)
        {
            try
            {
                // Mover y renombrar son dos pasos de I/O separados sobre AudioItem (record
                // INMUTABLE, ver AudioItem.cs): tras MoveAudio, newAudioVm.Model todavía apunta a
                // la ruta VIEJA (el archivo ya no está ahí). Por eso se refresca y se vuelve a
                // localizar el audio DESPUÉS de mover y ANTES de intentar renombrar — si se
                // encadenaran sin refrescar, RenameAudio fallaría con "no se encuentra el archivo".
                if (RecordingSaveDefaults.NeedsMove(currentProjectName, picked.ProjectName))
                {
                    var targetProject = picked.ProjectName is null
                        ? Projects.First(p => p.IsGeneral)
                        : Projects.First(p => !p.IsGeneral && p.Name == picked.ProjectName);
                    _workspace.MoveAudio(newAudioVm.Model, targetProject.Model);

                    RefreshAudios();
                    var movedPath = Path.Combine(targetProject.Model.FolderPath, newAudioVm.FileName);
                    newAudioVm = FindAudioByFullPath(movedPath) ?? newAudioVm;
                }

                if (!string.IsNullOrWhiteSpace(picked.Title) &&
                    RecordingSaveDefaults.NeedsRename(RecordingSaveDefaults.DefaultTitle(newAudioVm.FileName), picked.Title))
                {
                    var folder = Path.GetDirectoryName(newAudioVm.FullPath)!;
                    var ext = Path.GetExtension(newAudioVm.FileName);
                    _workspace.RenameAudio(newAudioVm.Model, picked.Title);

                    RefreshAudios();
                    var renamedPath = Path.Combine(folder, Workspace.Sanitize(picked.Title) + ext);
                    newAudioVm = FindAudioByFullPath(renamedPath) ?? newAudioVm;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"No se pudo guardar con el proyecto/título elegidos: {ex.Message}";
            }
        }

        // Seleccionar el audio recién grabado y ofrecer transcribirlo ya mismo, reusando el mismo
        // pipeline que el botón "Transcribir" (local o Groq, según el motor elegido).
        SelectedAudio = newAudioVm;
        newAudioVm.IsSelected = true;

        if (Confirm($"Grabación lista ('{newAudioVm.FileName}'). ¿Transcribirla ahora?"))
            await TranscribeAsync();
    }

    /// <summary>Busca el <see cref="AudioItemVm"/> cuyo archivo de audio está en <paramref name="fullPath"/>.</summary>
    private AudioItemVm? FindAudioByFullPath(string fullPath)
    {
        foreach (var p in Projects)
            foreach (var a in p.Audios)
                if (string.Equals(a.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                    return a;
        return null;
    }

    // ---- Productividad --------------------------------------------------

    private bool HasTranscriptText() => !string.IsNullOrWhiteSpace(TranscriptText);

    /// <summary>
    /// Copia el transcript al portapapeles en texto plano + HTML (párrafos): pegar en Word/Docs/
    /// Gmail conserva estructura básica en vez de perder todo el formato (ver
    /// <see cref="ClipboardService"/>/<see cref="ClipboardHtmlBuilder"/>, brief "Copiar con
    /// formato"). Antes solo copiaba texto plano (<c>Clipboard.SetText</c>) -- aditivo, el texto
    /// plano sigue siendo idéntico para cualquier editor que no entienda HTML.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTranscriptText))]
    private void CopyTranscript()
    {
        try
        {
            ClipboardService.CopyPlainAndHtml(TranscriptText, ClipboardHtmlBuilder.TextToHtmlFragment(TranscriptText));
            StatusMessage = "Transcript copiado al portapapeles.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo copiar: {ex.Message}";
        }
    }

    /// <summary>
    /// Texto tal cual se leyó del disco para el audio seleccionado. Es el baseline de
    /// <see cref="IsTranscriptDirty"/>: se setea SIEMPRE junto con <see cref="TranscriptText"/>
    /// (ver <c>OnSelectedAudioChanged</c> y <see cref="SaveTranscript"/>), nunca por separado.
    /// </summary>
    private string? _loadedTranscriptText;

    /// <summary>
    /// Hay ediciones sin guardar en el editor. Derivado, no un flag: ver
    /// <see cref="TranscriptDirtyState"/> — mismo criterio que <see cref="AlreadyPolished"/>.
    /// </summary>
    public bool IsTranscriptDirty => TranscriptDirtyState.IsDirty(_loadedTranscriptText, TranscriptText);

    /// <summary>
    /// Guarda lo que haya sin guardar ANTES de cambiar de audio.
    ///
    /// Arregla una pérdida de datos real (revisión de diseño 2026-07-16): `OnSelectedAudioChanged`
    /// pisaba el editor con el texto del audio nuevo sin chequear nada. Corregías veinte minutos de
    /// una reunión, clickeabas otro audio, y se iba todo — sin aviso, sin autosave, sin Ctrl+S.
    ///
    /// Guarda en silencio en vez de preguntar, por dos razones:
    /// 1. Un diálogo "¿querés guardar?" al que se le dice "no" exige REVERTIR la selección del
    ///    TreeView, y eso vuelve a disparar OnTreeSelectionChanged con el audio viejo, que sigue
    ///    sucio, que vuelve a preguntar: loop. Haría falta un flag de reentrada — o sea, arreglar
    ///    una pérdida de datos metiendo un colgado. Esto no puede loopear: no hay camino de vuelta.
    /// 2. Es lo que la usuaria espera igual. El texto es un .txt en SU carpeta; nadie quiere que su
    ///    trabajo dependa de acordarse de apretar Guardar.
    ///
    /// El aviso va por StatusMessage: guardar en silencio está bien, guardar en secreto no.
    /// </summary>
    private void SaveDirtyTranscript()
    {
        if (!IsTranscriptDirty || _workspace is null || SelectedAudio is null)
            return;

        var nombre = SelectedAudio.FileName;
        SaveTranscript();
        StatusMessage = $"Se guardaron los cambios de \"{nombre}\".";
    }

    /// <summary>Guarda los cambios del transcript editado en su .txt.</summary>
    private bool CanSaveTranscript() => SelectedAudio is not null && HasTranscriptText();

    [RelayCommand(CanExecute = nameof(CanSaveTranscript))]
    private void SaveTranscript()
    {
        if (_workspace is null || SelectedAudio is null)
            return;
        try
        {
            // La subcarpeta de transcripts del proyecto puede no existir todavía (p.ej. proyecto
            // recién creado): Workspace.SaveTranscript la crea antes de escribir.
            _workspace.SaveTranscript(SelectedAudio.TranscriptPath, TranscriptText);
            SelectedAudio.HasTranscript = true;
            // El baseline pasa a ser lo que quedó en disco: sin esto el texto queda "sucio" para
            // siempre y SaveDirtyTranscript re-guardaría en cada click del árbol.
            _loadedTranscriptText = TranscriptText;
            StatusMessage = "Cambios guardados.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo guardar: {ex.Message}";
        }
    }

    /// <summary>
    /// Busy flag PROPIO de "Mejorar texto", no comparte <see cref="IsBusy"/>: ese flag está acoplado
    /// a la barra de progreso/Cancelar/taskbar de la transcripción (ver XAML y los
    /// [NotifyPropertyChangedFor] de IsBusy más arriba), que no aplica acá. Mismo criterio que
    /// <see cref="IsSearching"/> para Búsqueda.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PolishTextCommand))]
    private bool _isPolishingText;

    /// <summary>
    /// Texto EXACTO que quedó mejorado (null si todavía no se mejoró). No se expone como booleano
    /// propio: "¿ya está mejorado?" se DERIVA comparándolo contra el texto actual (ver
    /// <see cref="AlreadyPolished"/>), así editar el transcript re-habilita el botón solo, sin un
    /// flag aparte que se pueda desincronizar. Vive en memoria a propósito: recordarlo entre
    /// sesiones necesitaría persistirlo, y el caso que importa (apretar dos veces seguidas) ya
    /// queda cubierto.
    /// </summary>
    private string? _polishedText;

    /// <summary>
    /// El texto actual es exactamente el que ya se mejoró. Volver a mandarlo al modelo gasta cuota
    /// diaria y cada pasada corre un poco más el texto de lo que se dijo (deriva) — sin ganancia.
    /// </summary>
    public bool AlreadyPolished => _polishedText is not null && _polishedText == TranscriptText;

    /// <summary>Texto del botón "Mejorar texto" según el estado.</summary>
    public string PolishButtonText => AlreadyPolished ? "Texto mejorado ✓" : "Mejorar texto";

    /// <summary>Tooltip del botón: con el botón apagado, explica POR QUÉ (ver el tooltip fijo que
    /// mentía en "Transcribir", mismo criterio de no dejar un control apagado sin explicación).</summary>
    public string PolishTooltip => AlreadyPolished
        ? "Este texto ya está mejorado. Si lo editás, vas a poder mejorarlo de nuevo."
        : "Corrige términos con tu vocabulario y agrega puntuación y párrafos. En transcripciones largas puede tardar un rato.";

    // Un resultado PARCIAL no marca el texto como mejorado: ahí reintentar sí sirve.
    private bool CanPolishText() => !IsPolishingText && HasTranscriptText() && !AlreadyPolished;

    /// <summary>
    /// "Mejorar texto" (ver <see cref="AiPolishClient"/>, <c>POST /api/polish</c>): corrige términos
    /// con el vocabulario del usuario y agrega puntuación/párrafos al transcript actual. Pensado
    /// sobre todo para el camino Local (Whisper en la PC) que, a diferencia de la nube, NO pasa el
    /// texto por el corrector de vocabulario al transcribir. El servidor puede partir textos largos
    /// en varios tramos -- si alguno no se pudo pulir queda como estaba (no se pierde contenido),
    /// avisado acá comparando <see cref="AiPolishResultDto.PolishedChunks"/> contra
    /// <see cref="AiPolishResultDto.TotalChunks"/>. Reusa <see cref="SaveTranscript"/> para persistir
    /// el resultado en el .txt del audio seleccionado (mismo archivo que el botón "Guardar" -- no
    /// duplica esa lógica; si no hay audio seleccionado, SaveTranscript no hace nada y el texto
    /// mejorado queda solo en el editor, igual que pasaría hoy si tipeás sin haber elegido un audio).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPolishText))]
    private async Task PolishTextAsync()
    {
        IsPolishingText = true;
        StatusMessage = "Mejorando el texto…";
        try
        {
            var token = await GetAiAccessTokenOrThrowAsync("mejorar el texto");
            var client = new AiPolishClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            var result = await client.PolishAsync(TranscriptText, SelectedAudio?.RemoteId, token, CancellationToken.None);

            var savedToDisk = SelectedAudio is not null;
            TranscriptText = result.Text;
            SaveTranscript();

            // Las reglas viven en PolishState (Core, puro y testeado), no duplicadas acá.
            var nothingToDo = PolishState.NothingToDo(result.TotalChunks);
            var partial = PolishState.IsPartial(result.PolishedChunks, result.TotalChunks);

            if (PolishState.ShouldMarkPolished(result.PolishedChunks, result.TotalChunks))
                MarkPolished(result.Text);

            if (nothingToDo)
                StatusMessage = "No hacía falta mejorar nada: el texto ya estaba bien.";
            else if (partial && savedToDisk)
                StatusMessage = "Texto mejorado parcialmente y guardado: alguna parte no se pudo corregir, pero no se perdió contenido.";
            else if (partial)
                StatusMessage = "Texto mejorado parcialmente: alguna parte no se pudo corregir, pero no se perdió contenido.";
            else if (savedToDisk)
                StatusMessage = "Texto mejorado y guardado.";
            else
                StatusMessage = "Texto mejorado.";
        }
        catch (Exception ex)
        {
            StatusMessage = FriendlyAiErrorMessage(ex);
        }
        finally
        {
            IsPolishingText = false;
        }
    }

    /// <summary>
    /// Marca <paramref name="text"/> como el texto ya mejorado y refresca lo que depende de eso.
    /// `_polishedText` es un campo común (no [ObservableProperty]) porque nunca se bindea solo:
    /// lo que la UI mira es <see cref="AlreadyPolished"/>/<see cref="PolishButtonText"/>.
    /// </summary>
    private void MarkPolished(string text)
    {
        _polishedText = text;
        OnPropertyChanged(nameof(AlreadyPolished));
        OnPropertyChanged(nameof(PolishButtonText));
        OnPropertyChanged(nameof(PolishTooltip));
        PolishTextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Abre la carpeta de transcripts en el Explorador.</summary>
    private bool CanOpenTranscripts() => _workspace is not null;

    [RelayCommand(CanExecute = nameof(CanOpenTranscripts))]
    private void OpenTranscriptsFolder()
    {
        if (_workspace is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _workspace.TranscriptsPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir la carpeta: {ex.Message}";
        }
    }

    // ---- Reproductor ----------------------------------------------------

    /// <summary>Volumen de reproducción (0.0–1.0). Arranca al 50% para no aturdir.</summary>
    [ObservableProperty]
    private double _volume = 0.5;

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = (float)value;
        _settings.Volume = value;
        _settings.Save();
    }

    /// <summary>Posición actual de reproducción, en segundos (para la barra).</summary>
    [ObservableProperty]
    private double _playbackPosition;

    /// <summary>Duración total del audio en reproducción, en segundos.</summary>
    [ObservableProperty]
    private double _playbackDuration;

    /// <summary>Texto "mm:ss / mm:ss" del avance de reproducción.</summary>
    [ObservableProperty]
    private string _playbackTimeText = "00:00 / 00:00";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayAudioCommand))]
    private bool _isPlaying;

    private void UpdatePlaybackPosition()
    {
        if (_isSeeking)
            return;
        PlaybackPosition = _player.CurrentTime.TotalSeconds;
        PlaybackDuration = _player.TotalTime.TotalSeconds;
        PlaybackTimeText = $"{Fmt(_player.CurrentTime)} / {Fmt(_player.TotalTime)}";
    }

    private static string Fmt(TimeSpan t) => t.ToString(@"mm\:ss");

    // SelectedAudio is { HasAudio: true }: no hay nada que reproducir en una transcripción SOLO
    // TEXTO (ver AudioItemVm.HasAudio).
    private bool CanPlayAudio() => SelectedAudio is { HasAudio: true } && !IsPlaying;

    /// <summary>Reproduce el audio seleccionado. La carga corre en segundo plano (no congela la UI).</summary>
    [RelayCommand(CanExecute = nameof(CanPlayAudio))]
    private async Task PlayAudio()
    {
        if (SelectedAudio is null)
            return;
        if (!SelectedAudio.HasAudio)
        {
            // Defensa en profundidad: CanPlayAudio ya lo filtra.
            StatusMessage = "Esta transcripción no tiene audio (se creó como solo texto).";
            return;
        }
        var audio = SelectedAudio;
        try
        {
            _player.Volume = (float)Volume;
            StatusMessage = $"Preparando '{audio.FileName}'…";
            // La decodificación (OGG puede tardar) va en background para no congelar la UI.
            await Task.Run(() => _player.Play(audio.FullPath));
            IsPlaying = true;
            _playbackTimer.Start();
            StatusMessage = $"Reproduciendo '{audio.FileName}' (volumen {Volume * 100:0}%)…";
        }
        catch (Exception ex)
        {
            IsPlaying = false;
            StatusMessage = $"No se pudo reproducir: {ex.Message}";
        }
    }

    /// <summary>Detiene la reproducción y resetea la barra.</summary>
    [RelayCommand]
    private void StopAudio()
    {
        _playbackTimer.Stop();
        _player.Stop();
        IsPlaying = false;
        PlaybackPosition = 0;
        PlaybackTimeText = PlaybackDuration > 0
            ? $"00:00 / {Fmt(TimeSpan.FromSeconds(PlaybackDuration))}"
            : "00:00 / 00:00";
    }

    // Llamados desde el code-behind al arrastrar la barra de progreso.
    public void BeginSeek() => _isSeeking = true;

    public void EndSeek(double seconds)
    {
        _player.Seek(TimeSpan.FromSeconds(seconds));
        PlaybackPosition = seconds;
        _isSeeking = false;
    }

    // ---- Exportar a Obsidian / Drive ------------------------------------

    private string _lastExportedMd = string.Empty;

    /// <summary>Elige la carpeta de exportación (vault de Obsidian o carpeta de Drive).</summary>
    [RelayCommand]
    private void ChooseExportFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Elegí tu carpeta de Obsidian (vault) o de Drive" };
        if (dialog.ShowDialog() == true)
            ExportFolder = dialog.FolderName;
    }

    private bool CanExport() =>
        SelectedAudio is not null &&
        !string.IsNullOrWhiteSpace(TranscriptText) &&
        !string.IsNullOrWhiteSpace(ExportFolder);

    /// <summary>Exporta el transcript actual como .md (título, contexto, metadata) a la carpeta destino.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportNote()
    {
        var path = ExportToDestinations(SelectedAudio!, TranscriptText);
        if (path is not null)
            StatusMessage = $"Nota exportada: {path}";
    }

    /// <summary>
    /// Escribe el .md en la carpeta destino (vault de Obsidian o carpeta de Drive Desktop).
    /// Devuelve la ruta escrita, o null si no hay carpeta configurada.
    /// </summary>
    private string? ExportToDestinations(AudioItemVm audio, string text)
    {
        if (string.IsNullOrWhiteSpace(ExportFolder))
            return null;
        try
        {
            var date = DateTime.Now;
            // IsGroq ? GroqModel : $"whisper-{LocalModelId} (local)" -- antes decía siempre
            // "whisper-small (local)", hardcodeado; con LocalModelId="small" (default) el texto
            // sigue siendo BYTE A BYTE el mismo de antes, así que una nota exportada antes de este
            // cambio y otra exportada después con el default sin tocar tienen el mismo metadata.
            var meta = new TranscriptMetadata(
                date, audio.FileName, audio.SizeText,
                Engine, IsGroq ? GroqModel : $"whisper-{LocalModelId} (local)");
            var content = MarkdownExporter.BuildMarkdown(meta, text, NoteTitle, NoteContext);
            var fileName = MarkdownExporter.BuildFileName(audio.FileName, date, _settings.ExportDateInName, NoteTitle);

            Directory.CreateDirectory(ExportFolder);
            var path = Path.Combine(ExportFolder, fileName);
            File.WriteAllText(path, content);
            _lastExportedMd = path;
            OpenInObsidianCommand.NotifyCanExecuteChanged();
            return path;
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo exportar: {ex.Message}";
            return null;
        }
    }

    private bool CanOpenInObsidian() =>
        (SelectedAudio is not null && !string.IsNullOrWhiteSpace(TranscriptText) && !string.IsNullOrWhiteSpace(ExportFolder))
        || !string.IsNullOrEmpty(_lastExportedMd);

    /// <summary>Exporta (si hace falta) y abre la nota en Obsidian vía el esquema obsidian://.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInObsidian))]
    private void OpenInObsidian()
    {
        // Aseguramos que la nota exista en disco (Obsidian abre archivos locales).
        var path = _lastExportedMd;
        if (SelectedAudio is not null && !string.IsNullOrWhiteSpace(TranscriptText) && !string.IsNullOrWhiteSpace(ExportFolder))
            path = ExportToDestinations(SelectedAudio, TranscriptText) ?? path;

        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            var uri = "obsidian://open?path=" + Uri.EscapeDataString(path);
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
            StatusMessage = "Abriendo en Obsidian…";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir en Obsidian (¿está instalado y es un vault?): {ex.Message}";
        }
    }

    // ---- Liberación de recursos al cerrar la app de verdad ---------------------------------
    // NO se llama al minimizar a la bandeja (ahí la app sigue viva en segundo plano: grabación,
    // sync y el watcher de archivos deben seguir funcionando). Solo se llama desde
    // App.OnMainWindowClosing cuando la acción resuelta es Exit real.

    private bool _disposed;

    /// <summary>
    /// Detiene grabación/reproducción/watcher/timers en curso antes del cierre real de la app.
    /// Si el usuario cerraba desde la bandeja mientras grababa, <see cref="MicrophoneRecorder.Stop"/>
    /// nunca se llamaba y el WAV en curso quedaba sin flush/cierre — este método lo garantiza.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        SyncCoordinator.Instance.SyncApplied -= OnSyncApplied;
        UpdateService.Instance.UpdateReady -= OnUpdateReady;

        _elapsedTimer.Stop();
        _refreshDebounce.Stop();
        _playbackTimer.Stop();
        _recordingTimer.Stop();

        // MicrophoneRecorder.Dispose() ya hace Stop() internamente si estaba grabando (flushea y
        // cierra el WAV en curso antes de soltar el dispositivo). MeetingRecorder.Dispose() hace
        // lo mismo -- ver su Dispose/Stop en Core/Audio.
        _recorder?.Dispose();
        _meetingRecorder?.Dispose();
        _player.Dispose();

        _watcher?.Dispose();
        _watcher = null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
