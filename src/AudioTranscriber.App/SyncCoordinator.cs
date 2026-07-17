using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AudioTranscriber.Core.Observability;
using AudioTranscriber.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Sentry;

namespace AudioTranscriber.App;

/// <summary>
/// Dueño único de "la carpeta" (workspace == carpeta sincronizada, estilo Dropbox) y de todo su
/// ciclo de vida de sync en segundo plano: <see cref="FileSystemWatcher"/> con debounce, pull
/// periódico, sync al iniciar, y el "Sincronizar ahora" manual. Instancia compartida
/// (<see cref="Instance"/>): MainWindow, SyncWindow y el menú de la bandeja se atan a la MISMA
/// instancia en vez de mantener estado independiente, para que el estado (Sincronizado /
/// Sincronizando… / Error) sea siempre consistente en toda la app.
/// <para/>
/// La lógica de debounce/coalescing y el freno anti-loop (para no reaccionar a los propios
/// escrituras del sync) viven en Core (<see cref="DebounceCoalescer"/>, <see cref="SyncLoopGuard"/>,
/// <see cref="SyncWatchFilter"/>) como piezas puras testeadas; acá solo se conectan a WPF
/// (<see cref="FileSystemWatcher"/>, <see cref="DispatcherTimer"/>, diálogos).
/// </summary>
public sealed partial class SyncCoordinator : ObservableObject
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PeriodicPullInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Carpeta base para el log de errores de sync (fuera del workspace sincronizado, para no
    /// terminar sincronizando el propio log). SpecialFolder.LocalApplicationData en vez de una ruta
    /// fija para que funcione en cualquier usuario/máquina. El log de sync sigue el mismo esquema
    /// por día que crash-*.log/close-*.log (ver <see cref="SyncErrorLogFormatter"/>): antes escribía
    /// a un único archivo acumulativo fuera de la carpeta "logs", lo que lo hacía invisible para
    /// quien solo miraba esa carpeta.
    /// </summary>
    private static string LocalAppDataPath =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Ruta del log de sync del día de hoy.</summary>
    private static string TodayLogFilePath =>
        SyncErrorLogFormatter.ResolveLogFilePath(LocalAppDataPath, DateTime.Now);

    // IMPORTANTE: declarar Instance DESPUÉS de las constantes de arriba. Los inicializadores de
    // campos estáticos corren en orden de declaración y el constructor usa esas TimeSpan; si
    // Instance va primero, valen aún TimeSpan.Zero y DebounceCoalescer tira por delay <= 0.
    public static SyncCoordinator Instance { get; } = new();

    private readonly HttpClient _http = new();
    private readonly AppSettings _settings;
    private readonly DebounceCoalescer _debouncer = new(DebounceDelay);
    private readonly SyncLoopGuard _loopGuard = new(SettleWindow);

    /// <summary>
    /// Serializa ciclos completos de sync (incluido el refresh de token dentro de
    /// <see cref="EnsureValidAccessTokenAsync"/>). El watcher (~2.5s debounce), el timer
    /// periódico (~60s) y el sync de arranque pueden dispararse casi en simultáneo; sin este
    /// freno, dos ciclos podían llamar a <c>RefreshAsync</c> en paralelo y, como Supabase rota
    /// el refresh token en cada uso, uno de los dos terminaba usando un token ya invalidado por
    /// el otro (400 "Autenticación falló"). Un disparo que llega mientras otro ciclo ya está
    /// corriendo se descarta (no se encola ni se espera), igual criterio que ya usaba <see cref="IsBusy"/>.
    /// </summary>
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly DispatcherTimer _debounceTicker;
    private readonly DispatcherTimer _periodicTimer;

    private FileSystemWatcher? _watcher;
    private bool _started;

    private SyncCoordinator()
    {
        _settings = AppSettings.Instance;
        _syncFolder = _settings.SyncFolder;
        _isLoggedIn = HasStoredSession();

        // Precarga del perfil (nombre/email/avatar) persistido en el último login exitoso, para
        // que SyncWindow lo tenga disponible ya en el primer render sin esperar un RefreshLoginState.
        if (_isLoggedIn)
        {
            _userName = _settings.UserName;
            _userEmail = _settings.UserEmail;
            _userAvatarUrl = _settings.UserAvatarUrl;
        }

        // Tick corto: solo consulta si el debounce ya venció (la lógica real vive en Core).
        _debounceTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounceTicker.Tick += (_, _) =>
        {
            if (_debouncer.TryConsumeDue(DateTimeOffset.UtcNow))
                _ = RunSyncAsync(manual: false);
        };

        _periodicTimer = new DispatcherTimer { Interval = PeriodicPullInterval };
        _periodicTimer.Tick += (_, _) => _ = RunSyncAsync(manual: false);
    }

    /// <summary>Avisa que se aplicaron cambios de un sync (para que MainViewModel refresque el árbol).</summary>
    public event Action? SyncApplied;

    /// <summary>La única carpeta: workspace + sync.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    [NotifyPropertyChangedFor(nameof(IsUnconfigured))]
    private string _syncFolder;

    /// <summary>
    /// Todavía no se eligió carpeta. Es el MISMO criterio que usa <see cref="DisplayStatus"/> para
    /// decidir el texto "Sin configurar" (ver abajo), expuesto como bool para que la UI no tenga que
    /// adivinarlo del copy.
    ///
    /// Existe por un bug real (revisión de diseño 2026-07-16): el punto de estado de MainWindow
    /// decidía su color con un DataTrigger que comparaba el TEXTO VISIBLE contra el literal
    /// "Sin configurar", y el default de ese Style es VERDE. O sea que mejorar ese copy — que hay
    /// que mejorarlo, hoy el mismo concepto tiene cinco nombres — dejaba el trigger sin matchear y
    /// el punto se ponía verde de "sincronizado" mientras no había nada configurado. El usuario
    /// creería que sus grabaciones están respaldadas cuando no lo están: la peor clase de mentira
    /// de UI, la tranquilizadora.
    ///
    /// El copy es para las personas; los bindings van contra el estado. Nunca al revés.
    /// </summary>
    public bool IsUnconfigured => string.IsNullOrWhiteSpace(SyncFolder);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private bool _isBusy;

    /// <summary>Mensaje detallado (para el cuadro de estado de SyncWindow).</summary>
    [ObservableProperty]
    private string _statusMessage = "Listo.";

    [ObservableProperty]
    private bool _isLoggedIn;

    /// <summary>Nombre para mostrar del usuario logueado. Vacío si no hay sesión.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitials))]
    private string _userName = string.Empty;

    /// <summary>Email del usuario logueado. Vacío si no hay sesión.</summary>
    [ObservableProperty]
    private string _userEmail = string.Empty;

    /// <summary>URL de la foto de perfil (Google). "" si no hay sesión o el proveedor no la mandó.</summary>
    [ObservableProperty]
    private string _userAvatarUrl = string.Empty;

    /// <summary>Iniciales para el placeholder del avatar; ver <see cref="UserProfileFormatter.GetInitials"/>.</summary>
    public string UserInitials => UserProfileFormatter.GetInitials(UserName);

    /// <summary>
    /// True cuando el último intento de sync falló por sesión inválida/expirada (401/400 de
    /// Supabase Auth, incluido "refresh_token_not_found"). Se usa para mostrar un estado
    /// específico ("Iniciá sesión") en vez del "Error" genérico, y para que el botón de
    /// "Sincronizar ahora" manual abra el login directamente en vez de solo avisar.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private bool _needsLogin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private DateTimeOffset? _lastSyncAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private bool _hasError;

    /// <summary>
    /// Categoría del último error de sync (ver <see cref="SyncErrorClassifier"/>), usada por
    /// <see cref="DisplayStatus"/> para mostrar un chip específico (p.ej. "Sin conexión", "Error del
    /// servidor, reintentando…") en vez de un "Error" genérico cuando <see cref="HasError"/> es true
    /// y <see cref="NeedsLogin"/> es false. Se reinicia a <see cref="SyncErrorCategory.Unknown"/> al
    /// arrancar cada ciclo de sync.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private SyncErrorCategory _lastErrorCategory = SyncErrorCategory.Unknown;

    /// <summary>
    /// Detalle del último error de sync (tipo + mensaje real de la excepción, incluyendo el
    /// código HTTP y el cuerpo de la respuesta del backend cuando aplica). Vacío si no hay error.
    /// Se muestra en SyncWindow debajo del estado; el mismo detalle (con stack trace completo y la
    /// categoría clasificada) queda además en el log de sync del día vía <see cref="LogSyncError"/>.
    /// </summary>
    [ObservableProperty]
    private string _lastError = string.Empty;

    /// <summary>Etiqueta corta para chips de estado (footer de MainWindow, menú de la bandeja).</summary>
    public string DisplayStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SyncFolder))
                return "Sin configurar";
            if (IsBusy)
                return "Sincronizando…";
            if (NeedsLogin)
                return SyncErrorMessages.ChipFor(SyncErrorCategory.NeedsLogin);
            if (HasError)
                return SyncErrorMessages.ChipFor(LastErrorCategory);
            return LastSyncAt is { } t ? $"Sincronizado ✓ · {t:HH:mm}" : "Sincronizado ✓";
        }
    }

    partial void OnSyncFolderChanged(string value)
    {
        _settings.SyncFolder = value;
        _settings.Save();
    }

    public bool HasStoredSession() =>
        !string.IsNullOrEmpty(SecureStore.LoadSecret(SecureStore.SyncRefreshTokenKey));

    /// <summary>
    /// HttpClient compartido, para reusar desde llamadas fuera del ciclo de sync (p.ej. la
    /// transcripción en la nube de <c>MainViewModel</c>) en vez de instanciar uno nuevo.
    /// </summary>
    public HttpClient Http => _http;

    /// <summary>
    /// Devuelve un access token de sesión válido, refrescándolo primero si hace falta (mismo
    /// criterio que ya usa el propio ciclo de sync vía <see cref="EnsureValidAccessTokenAsync"/>).
    /// Null si no hay ninguna sesión guardada. Pensado para reusar la lógica de expiración/refresh
    /// desde afuera del ciclo de sync sin duplicarla (p.ej. modo "Groq (nube)" interactivo).
    /// </summary>
    public async Task<string?> GetValidAccessTokenAsync()
    {
        var accessToken = SecureStore.LoadSecret(SecureStore.SyncAccessTokenKey);
        var refreshToken = SecureStore.LoadSecret(SecureStore.SyncRefreshTokenKey);
        if (string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(refreshToken))
            return null;
        return await EnsureValidAccessTokenAsync(accessToken, refreshToken);
    }

    public void RefreshLoginState()
    {
        IsLoggedIn = HasStoredSession();
        if (IsLoggedIn)
        {
            NeedsLogin = false;
            // LoginWindow ya persistió esto en AppSettings.Instance (misma instancia que _settings)
            // antes de que RefreshLoginState() se llame, así que alcanza con releerlo de acá.
            UserName = _settings.UserName;
            UserEmail = _settings.UserEmail;
            UserAvatarUrl = _settings.UserAvatarUrl;
        }
    }

    // ---- Onboarding -----------------------------------------------------------

    [RelayCommand]
    private void Login() => LoginInternal();

    /// <summary>
    /// "Cerrar sesión" desde SyncWindow: borra la sesión persistida (tokens + perfil) y vuelve al
    /// estado no logueado. A diferencia del logout forzado por sesión inválida (ver
    /// <see cref="RunSyncAsync"/>), este es siempre una acción explícita del usuario, así que no
    /// toca <see cref="HasError"/>/<see cref="NeedsLogin"/> ni dispara un re-login automático.
    /// </summary>
    [RelayCommand]
    private void Logout()
    {
        ClearSessionAndLogOut();
        NeedsLogin = false;
        StatusMessage = "Sesión cerrada.";
    }

    /// <summary>Usado por el onboarding: si ya hay sesión no hace nada; si no, abre el login.</summary>
    public bool EnsureLoggedIn() => IsLoggedIn || HasStoredSession() || LoginInternal();

    private bool LoginInternal()
    {
        var loggedIn = LoginWindow.Show(_http);
        // RefreshLoginState() (en vez de asignar IsLoggedIn a mano) es la única fuente de verdad
        // de "hay sesión guardada": lee directo de SecureStore, así queda consistente sin importar
        // si loggedIn vino de LoginWindow o de una sesión que ya estaba persistida.
        RefreshLoginState();
        if (loggedIn)
        {
            NeedsLogin = false;
            HasError = false;
            StatusMessage = "Sesión iniciada correctamente.";
        }
        return loggedIn;
    }

    /// <summary>
    /// Elige carpeta y la deja como workspace+sync activo. Null si se canceló el diálogo. Único
    /// selector de carpeta de toda la app (ver MainViewModel.OpenWorkspaceCommand y el comentario
    /// en SyncWindow.xaml) — no exponer un segundo picker independiente.
    /// </summary>
    public string? ChooseFolderAndStart()
    {
        // A.6/B.3 (DESIGN-REVIEW-2026-07-16.md): antes decía "(tu workspace)" -- jerga cruda en
        // inglés, justo en el picker de onboarding. "tu carpeta" es el mismo nombre que usa el
        // resto de la app para este mismo objeto en disco.
        var dialog = new OpenFolderDialog { Title = "Elegí tu carpeta" };
        if (dialog.ShowDialog() != true)
            return null;
        SetFolder(dialog.FolderName);
        return dialog.FolderName;
    }

    /// <summary>
    /// Arranca (o reanuda) el watcher/timers para <paramref name="path"/> sin cambiar la carpeta
    /// configurada; se usa al abrir la app con una carpeta ya elegida en una sesión anterior.
    /// Dispara el sync de arranque una sola vez por vida del proceso.
    /// </summary>
    public void Start(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        AttachWatcher(path);
        _debounceTicker.Start();
        _periodicTimer.Start();
        RefreshLoginState();

        if (!_started)
        {
            _started = true;
            _ = RunSyncAsync(manual: false);
        }
    }

    /// <summary>El usuario eligió (o cambió) la carpeta explícitamente: persiste y sincroniza ya.</summary>
    public void SetFolder(string path)
    {
        // La carpeta tiene que EXISTIR antes de vigilarla: `FileSystemWatcher` no la crea, tira
        // ArgumentException("The directory name '...' does not exist. (Parameter 'path')").
        //
        // Bugfix 2026-07-15 (reportado por una usuaria real, instalación limpia): los dos
        // llamadores de MainViewModel (arrastrar archivos y "Grabar audio") hacían
        // SetFolder(default) ANTES de LoadWorkspace(default) — y LoadWorkspace es justamente quien
        // crea el árbol vía Workspace.OpenOrCreate. En una PC donde la carpeta ya existía de antes
        // nunca se notaba; en una instalación nueva reventaba SIEMPRE, en las dos acciones
        // principales de la app. Garantizarlo acá arregla a todos los llamadores de una y además
        // cubre que la carpeta desaparezca después (OneDrive la mueve, o la borran a mano).
        Directory.CreateDirectory(path);

        SyncFolder = path;
        AttachWatcher(path);
        _debounceTicker.Start();
        _periodicTimer.Start();
        RefreshLoginState();
        _started = true;
        _ = RunSyncAsync(manual: false);
    }

    private void AttachWatcher(string path)
    {
        if (_watcher is not null && string.Equals(_watcher.Path, path, StringComparison.OrdinalIgnoreCase))
            return;

        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                            | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        // Los eventos de FileSystemWatcher llegan en un hilo de threadpool, no el de UI: hay que
        // marshalear al Dispatcher antes de tocar el debouncer/loop guard (mismo patrón que usa
        // MainViewModel para su propio watcher de la carpeta 'audios').
        FileSystemEventHandler onChange = (_, e) =>
            Application.Current?.Dispatcher.Invoke(() => OnFileSystemEvent(path, e.FullPath));
        RenamedEventHandler onRenamed = (_, e) =>
            Application.Current?.Dispatcher.Invoke(() => OnFileSystemEvent(path, e.FullPath));

        _watcher.Created += onChange;
        _watcher.Changed += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += onRenamed;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileSystemEvent(string root, string fullPath)
    {
        // Anti-loop: ni las carpetas internas del cliente (.synccache/.papelera) ni los ecos de
        // las escrituras que hace el propio ciclo de sync deberían disparar otro ciclo.
        if (SyncWatchFilter.ShouldIgnore(root, fullPath))
            return;
        if (_loopGuard.ShouldIgnoreEvent(DateTimeOffset.UtcNow))
            return;

        _debouncer.Signal(DateTimeOffset.UtcNow);
    }

    // ---- Ejecución del sync -----------------------------------------------------

    private bool CanSyncNow() => !IsBusy;

    /// <summary>"Sincronizar ahora": forzado manual, único camino que confirma un borrado masivo.</summary>
    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    public async Task SyncNowAsync() => await RunSyncAsync(manual: true);

    private async Task RunSyncAsync(bool manual)
    {
        if (IsBusy)
            return;
        if (string.IsNullOrWhiteSpace(SyncFolder) || !Directory.Exists(SyncFolder))
        {
            if (manual)
                StatusMessage = "Elegí una carpeta de sync válida antes de sincronizar.";
            return;
        }

        var accessToken = SecureStore.LoadSecret(SecureStore.SyncAccessTokenKey);
        var refreshToken = SecureStore.LoadSecret(SecureStore.SyncRefreshTokenKey);
        if (string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(refreshToken))
        {
            NeedsLogin = true;
            if (manual)
            {
                // Sync manual sin sesión: abrir el login directo en vez de solo avisar y dejar
                // que el usuario tenga que ir a buscar el botón "Iniciar sesión" por su cuenta.
                StatusMessage = "Iniciá sesión antes de sincronizar.";
                if (LoginInternal())
                    _ = RunSyncAsync(manual: true);
            }
            return;
        }

        // No bloquear esperando el ciclo en curso: si ya hay uno corriendo, este disparo se
        // descarta (ver comentario de _syncGate más arriba).
        if (!await _syncGate.WaitAsync(0))
            return;

        IsBusy = true;
        HasError = false;
        LastError = string.Empty;
        LastErrorCategory = SyncErrorCategory.Unknown;
        StatusMessage = "Sincronizando…";

        // Breadcrumb (no un evento aparte): deja rastro del ciclo de sync para que, si algo
        // revienta más adelante en el mismo proceso, el reporte de Sentry tenga contexto de qué
        // pasó justo antes. No-op si Sentry no está habilitado.
        SentrySdk.AddBreadcrumb(
            message: "Sync iniciado",
            category: "sync",
            level: BreadcrumbLevel.Info,
            data: new Dictionary<string, string> { ["manual"] = manual.ToString() });

        _loopGuard.BeginSync();
        var retryWithLogin = false;
        try
        {
            accessToken = await EnsureValidAccessTokenAsync(accessToken, refreshToken);
            if (string.IsNullOrEmpty(accessToken))
            {
                NeedsLogin = true;
                StatusMessage = "La sesión expiró. Iniciá sesión de nuevo.";
                HasError = true;
                return;
            }

            var engine = BuildSyncEngine();
            var result = await engine.RunAsync(accessToken);

            // Diagnóstico del pull (bug real: transcripciones de la web que no bajaban al desktop
            // sin ningún rastro de por qué). No había forma de saber cuánto trajo el pull ni cuántas
            // descargas de audio fallaban -- SyncEngine ahora expone esos conteos en SyncResult
            // (ver AudioDownloadFailures), acá se dejan como breadcrumb (mismo patrón ya establecido
            // para diagnóstico de sync, no es un error: las fallidas se reintentan solas el próximo
            // ciclo, ver comentario en SyncEngine.RunAsync).
            SentrySdk.AddBreadcrumb(
                message: "Sync: pull recibido",
                category: "sync",
                level: result.AudioDownloadFailures > 0 ? BreadcrumbLevel.Warning : BreadcrumbLevel.Info,
                data: new Dictionary<string, string>
                {
                    ["proyectos"] = result.PulledProjectsCount.ToString(),
                    ["transcripciones"] = result.PulledTranscriptionsCount.ToString(),
                    ["acciones"] = result.Actions.Count.ToString(),
                    ["audioFallidos"] = result.AudioDownloadFailures.ToString(),
                    ["audioMuyGrande"] = result.OversizeUploadSkips.ToString(),
                });

            // Logging SIEMPRE del ciclo (no solo en error, ver LogCycleSummary): pedido explícito
            // 2026-07-08 -- el usuario reportaba "Sincronizado, 2 acciones aplicadas" sin error pero
            // sin poder saber qué trajo el pull ni qué se aplicó/omitió y por qué.
            LogCycleSummary(manual, result);

            if (result.Outcome == SyncOutcome.ConfirmationPending)
            {
                if (!manual)
                {
                    // El sync automático NUNCA confirma un borrado masivo por su cuenta: se deja
                    // pendiente para que el usuario lo resuelva desde "Sincronizar ahora" (evita
                    // un MessageBox inesperado disparado en segundo plano).
                    StatusMessage = result.Message
                        ?? "Hay muchos borrados pendientes de confirmar: usá 'Sincronizar ahora'.";
                    HasError = true;
                    return;
                }

                var confirm = MessageBox.Show(
                    result.Message ?? $"Esto va a borrar {result.DeleteCount} de {result.BaselineCount} items. ¿Continuar?",
                    "Confirmar sincronización",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                {
                    StatusMessage = "Sincronización cancelada (borrado no confirmado).";
                    return;
                }

                result = await engine.RunAsync(accessToken, forceConfirmDeletes: true);
                LogCycleSummary(manual, result);
            }

            NeedsLogin = false;
            LastSyncAt = DateTimeOffset.Now;
            StatusMessage = result.Actions.Count > 0
                ? $"Sincronización completa: {result.Actions.Count} acción(es) aplicadas."
                : "Sincronizado ✓";

            // Audios que no entraron a la nube por tamaño (413) o formato (415): se avisa acá para
            // que el usuario sepa por qué esos audios no aparecen sincronizados, en vez de que
            // fallen en silencio y para siempre (ver SyncFailureClassifier / OversizeUploadSkips).
            if (result.OversizeUploadSkips > 0)
                StatusMessage += $" · {result.OversizeUploadSkips} audio(s) muy grande(s) quedaron solo en tu equipo.";

            // Rechazo de borrado en cascada (bug C1): el sync SÍ se completó (el resto de
            // push/pull siguió normal), pero uno o más proyectos quedaron sin poder borrarse
            // porque tienen subcarpetas en la nube. Se reusa el mismo panel de detalle que ya
            // existe para errores (LastError/HasError, ver SyncWindow.xaml) en vez de inventar
            // una notificación nueva -- no es un fallo del ciclo, así que no pasa por el catch.
            if (!string.IsNullOrEmpty(result.Message))
            {
                HasError = true;
                LastError = result.Message;
            }

            SentrySdk.AddBreadcrumb(
                message: "Sync completado",
                category: "sync",
                level: BreadcrumbLevel.Info,
                data: new Dictionary<string, string> { ["actions"] = result.Actions.Count.ToString() });

            if (result.Actions.Count > 0)
                SyncApplied?.Invoke();
        }
        catch (Exception ex)
        {
            HasError = true;
            LastError = $"{ex.GetType().Name}: {ex.Message}";

            // Clasificación pura (ver SyncErrorClassifier/SyncErrorMessages en Core, testeadas):
            // sesión inválida/vencida se distingue de un error genérico (red, 5xx, bug del cliente).
            // No debe quedar en "Error" opaco: NeedsLogin lleva a "Iniciá sesión" (problema 4) y no
            // muestra el panel de detalle de error (problema 3 es para los OTROS errores); los demás
            // ahora también tienen chip/mensaje específico en vez de "Error" genérico.
            var category = SyncErrorClassifier.Classify(ex);
            LastErrorCategory = category;
            var isInvalidSession = category == SyncErrorCategory.NeedsLogin;

            if (isInvalidSession)
            {
                // GUARDA anti-carrera: este ciclo arrancó con el refreshToken capturado al
                // principio del método. Si el usuario inició sesión de nuevo MIENTRAS este ciclo
                // seguía corriendo (p.ej. timer periódico + login manual casi al mismo tiempo), el
                // refresh token guardado en SecureStore ya cambió y NO es el que acaba de fallar acá.
                // Sin esta guarda, el 400/401 del ciclo viejo borraba la sesión nueva recién iniciada
                // (bug: "sigue diciendo que no hay sesión" justo después de loguearse).
                var currentRefreshToken = SecureStore.LoadSecret(SecureStore.SyncRefreshTokenKey);
                var supersededByNewerLogin = SyncSessionGuard.WasSupersededByNewerLogin(currentRefreshToken, refreshToken);

                if (supersededByNewerLogin)
                {
                    // No es un logout real: solo un ciclo viejo perdiendo la carrera contra un
                    // login nuevo. Dejar la sesión (ya válida) intacta y reintentar con ella.
                    HasError = false;
                    LastError = string.Empty;
                    NeedsLogin = false;
                    StatusMessage = "Se inició sesión de nuevo durante la sincronización; reintentando…";
                    retryWithLogin = true;
                }
                else
                {
                    // Rechazo definitivo: el token/sesión ya no sirve (inválido, vencido o rotado
                    // por otro ciclo) y reintentar con el mismo va a repetir el mismo fallo para
                    // siempre. Se limpia la sesión y se fuerza re-login en vez de dejar la UI
                    // trabada en "Error" genérico — por eso HasError se apaga acá: este NO es el
                    // camino de error normal (problema 3), es el de "hace falta iniciar sesión".
                    HasError = false;
                    LastError = string.Empty;
                    ClearSessionAndLogOut();
                    NeedsLogin = true;
                    StatusMessage = "La sesión expiró. Iniciá sesión de nuevo.";

                    // En sync manual, abrir el login directo en vez de un estado opaco que el
                    // usuario tiene que interpretar y resolver por su cuenta.
                    if (manual)
                        retryWithLogin = LoginInternal();
                }
            }
            else
            {
                StatusMessage = SyncErrorMessages.StatusMessageFor(category, ex.Message);
            }

            // Sentry: sesión inválida/vencida (400/401/403, o sin sesión) es un flujo esperado del
            // ciclo de vida del token, no un bug — mandarlo como evento completo cada vez generaría
            // ruido. Se deja solo como breadcrumb (contexto para un futuro evento real). Cualquier
            // OTRO error (red, 5xx, bug del cliente) sí se reporta como evento completo. No-op si
            // Sentry no está habilitado.
            if (isInvalidSession)
            {
                SentrySdk.AddBreadcrumb(
                    message: "Sync: sesión inválida o vencida (no se reporta como evento)",
                    category: "sync",
                    level: BreadcrumbLevel.Warning);
            }
            else
            {
                SentrySdk.CaptureException(ex);
            }

            LogSyncError(ex, category);
        }
        finally
        {
            IsBusy = false;
            _loopGuard.EndSync(DateTimeOffset.UtcNow);
            _syncGate.Release();
        }

        // Recién DESPUÉS de liberar _syncGate en el finally de arriba: reintentar acá adentro
        // (antes del release) haría deadlock, ya que RunSyncAsync vuelve a pedir el mismo gate.
        if (retryWithLogin)
            _ = RunSyncAsync(manual: true);
    }

    /// <summary>
    /// Borra la sesión persistida (access/refresh token y vencimiento) y actualiza
    /// <see cref="IsLoggedIn"/>. Se usa cuando Supabase rechaza el refresh token de forma
    /// definitiva (400): no tiene sentido reintentar con un token que ya fue invalidado.
    /// </summary>
    private void ClearSessionAndLogOut()
    {
        SecureStore.DeleteSecret(SecureStore.SyncAccessTokenKey);
        SecureStore.DeleteSecret(SecureStore.SyncRefreshTokenKey);
        SecureStore.DeleteSecret(SecureStore.SyncExpiresAtKey);
        IsLoggedIn = false;

        UserName = string.Empty;
        UserEmail = string.Empty;
        UserAvatarUrl = string.Empty;
        _settings.UserName = string.Empty;
        _settings.UserEmail = string.Empty;
        _settings.UserAvatarUrl = string.Empty;
        _settings.Save();
    }

    /// <summary>
    /// Escribe el detalle completo del error (timestamp, categoría clasificada, tipo, mensaje y
    /// stack trace -- formateado por <see cref="SyncErrorLogFormatter"/>, en Core y testeado) en el
    /// log de sync del día (<see cref="TodayLogFilePath"/>). Best-effort a propósito: un fallo acá
    /// (disco lleno, permisos, etc.) NUNCA debe tapar ni reemplazar el error real del sync que ya se
    /// mostró en la UI. No loguea tokens: el mensaje de la excepción trae el cuerpo de la RESPUESTA
    /// del backend, no el access/refresh token que viajó en el request.
    /// </summary>
    private void LogSyncError(Exception ex, SyncErrorCategory category)
    {
        try
        {
            var localAppData = LocalAppDataPath;
            var now = DateTime.Now;
            Directory.CreateDirectory(SyncErrorLogFormatter.ResolveLogDirectory(localAppData));
            var path = SyncErrorLogFormatter.ResolveLogFilePath(localAppData, now);
            var entry = SyncErrorLogFormatter.FormatEntry(now, category, ex);
            File.AppendAllText(path, entry);
        }
        catch
        {
            // Swallow: el logging es best-effort, no debe tirar abajo el ciclo de sync.
        }
        finally
        {
            // El botón "Ver log" habilita/deshabilita según exista el archivo de hoy; recién puede
            // haber pasado a existir arriba.
            ViewLogCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Escribe el RESUMEN de este ciclo (pull recibido, aplicadas, pusheadas, omitidas y por qué --
    /// ver <see cref="SyncResult.Diagnostics"/>) al mismo log de sync del día
    /// (<see cref="TodayLogFilePath"/>), formateado por <see cref="SyncCycleLogFormatter"/> (Core,
    /// testeado). A diferencia de <see cref="LogSyncError"/> (solo corre en el catch), este corre en
    /// CADA ciclo completado, tenga o no error, tenga o no cambios -- pedido explícito 2026-07-08:
    /// antes no había forma de diagnosticar "sync corre OK pero no aparece nada nuevo" sin reproducir
    /// el bug a mano. Best-effort a propósito, igual que <see cref="LogSyncError"/>.
    /// </summary>
    private void LogCycleSummary(bool manual, SyncResult result)
    {
        try
        {
            var localAppData = LocalAppDataPath;
            var now = DateTime.Now;
            Directory.CreateDirectory(SyncErrorLogFormatter.ResolveLogDirectory(localAppData));
            var path = SyncErrorLogFormatter.ResolveLogFilePath(localAppData, now);
            var entry = SyncCycleLogFormatter.FormatEntry(
                now, manual, result.Outcome,
                result.PulledProjectsCount, result.PulledTranscriptionsCount,
                result.Actions.Count, result.PushedCount, result.AudioDownloadFailures,
                result.Diagnostics ?? Array.Empty<string>());
            File.AppendAllText(path, entry);
        }
        catch
        {
            // Swallow: el logging es best-effort, no debe tirar abajo el ciclo de sync.
        }
        finally
        {
            ViewLogCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanViewLog() => File.Exists(TodayLogFilePath);

    /// <summary>Abre el log de sync de hoy con el visor de texto por defecto del sistema.</summary>
    [RelayCommand(CanExecute = nameof(CanViewLog))]
    private void ViewLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(TodayLogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo abrir el log: {ex.Message}";
        }
    }

    private SyncEngine BuildSyncEngine()
    {
        var index = new SyncIndex(SyncIndex.DefaultPathFor(SyncFolder));
        return new SyncEngine(
            new SyncApiClient(_http, SyncConfig.BackendBaseUrl),
            _http,
            index,
            new LocalScanner(),
            new RemoteMapper(),
            new SyncPlanner(),
            SyncFolder,
            SyncConfig.BackendBaseUrl);
    }

    /// <summary>
    /// Devuelve un access token válido, refrescándolo primero si está vencido o por vencer
    /// (<see cref="TokenExpiryPolicy"/>). Best-effort: si no hay refresh token, sigue con lo que
    /// haya (o null si no hay nada utilizable).
    /// <para/>
    /// <c>internal</c> (en vez de <c>private</c>) a propósito: las features de IA nativas
    /// (<c>NoteDetailViewModel</c>, ver "Híbrido nativo" 2026-07-13) lo reusan para conseguir un
    /// access token fresco antes de llamar a <c>/api/summarize</c>/<c>/api/recipes/apply</c>, en
    /// vez de reimplementar la misma lógica de refresh -- único cambio de visibilidad, el cuerpo
    /// del método no se tocó. Antes lo usaba el WebView2 embebido (eliminado en el mismo cambio).
    /// </summary>
    internal async Task<string?> EnsureValidAccessTokenAsync(string accessToken, string refreshToken)
    {
        var expiresAtRaw = SecureStore.LoadSecret(SecureStore.SyncExpiresAtKey);
        var expiresAt = long.TryParse(expiresAtRaw, out var parsed) ? parsed : 0L;

        if (!string.IsNullOrEmpty(accessToken) && !TokenExpiryPolicy.ShouldRefresh(DateTimeOffset.UtcNow, expiresAt))
            return accessToken;

        if (string.IsNullOrEmpty(refreshToken))
            return string.IsNullOrEmpty(accessToken) ? null : accessToken;

        var auth = new SupabaseAuthClient(_http, SyncConfig.SupabaseUrl, SyncConfig.SupabaseAnonKey);
        var session = await auth.RefreshAsync(refreshToken);

        SecureStore.SaveSecret(SecureStore.SyncAccessTokenKey, session.AccessToken);
        SecureStore.SaveSecret(SecureStore.SyncRefreshTokenKey, session.RefreshToken);
        SecureStore.SaveSecret(SecureStore.SyncExpiresAtKey, session.ExpiresAt.ToString());

        return session.AccessToken;
    }
}
