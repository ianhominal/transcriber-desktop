using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AudioTranscriber.App.ViewModels;
using AudioTranscriber.Core.Runtime;
using Sentry;
using Velopack;

namespace AudioTranscriber.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private TrayIconService? _trayIconService;

    /// <summary>
    /// Raw process launch arguments, captured in <see cref="Main"/> (the earliest point they're
    /// available) and read later from <see cref="OnStartup"/>. Not read from
    /// StartupEventArgs.Args there: with a custom Main/entry point (no StartupUri, see Main's
    /// XML doc), WPF's own population of that property isn't something this app wants to rely
    /// on — capturing the argv this app actually received is unambiguous.
    /// </summary>
    private static string[] _startupArgs = Array.Empty<string>();

    public App()
    {
        // Red de seguridad global: cualquier excepción no manejada muestra un
        // mensaje en vez de cerrar la app en silencio.
        DispatcherUnhandledException += OnUnhandledException;

        // AppDomain.UnhandledException y TaskScheduler.UnobservedTaskException ya los captura solo
        // Sentry (integraciones default de SentrySdk.Init, ver SentryBootstrap) — eso queda sin
        // tocar. Estos dos handlers son ADEMÁS, solo para que también escriban en el crash log
        // local (ver CrashLogger); no interceptan ni cambian el comportamiento de Sentry.
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Entry point custom (reemplaza el Main() que WPF genera automáticamente desde
    /// StartupUri/ApplicationDefinition — ver el ItemGroup de App.xaml en el .csproj). Hace falta
    /// para poder correr <see cref="VelopackApp.Build"/>().Run() ANTES de que arranque cualquier
    /// cosa de WPF: es lo primero que Velopack necesita para interceptar sus argumentos de hook
    /// (instalación/update/desinstalación) cuando el instalador relanza el .exe con esos flags.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        // Captured here (earliest point args are available) for OnStartup to read later — see
        // the field's XML doc for why OnStartup doesn't use StartupEventArgs.Args instead.
        _startupArgs = args;

        // Instrumentación de diagnóstico (bug 2026-07-08: la app sincronizaba en segundo plano pero
        // no mostraba ni ventana ni bandeja, sin NINGÚN rastro en ningún log). Todo lo que corre acá
        // pasa ANTES de que exista una instancia de App -- es decir, ANTES de que
        // DispatcherUnhandledException/AppDomain.CurrentDomain.UnhandledException estén suscriptos
        // (eso pasa recién en el constructor de App, más abajo). Si algo de esto tirara, hasta ahora
        // el proceso podía morir sin dejar ningún rastro; con StartupLogger (best-effort, nunca tira)
        // queda al menos un log de en qué paso pasó.
        StartupLogger.Log($"Main begin. Running assembly version: {RunningVersionText}. Args: [{string.Join(' ', args)}].");

        VelopackApp.Build().Run();
        StartupLogger.Log("VelopackApp.Build().Run() returned.");

        // Sentry (reporte de errores) arranca ANTES que cualquier cosa de WPF, para poder
        // capturar hasta el error más temprano. Es un no-op si no hay DSN configurado (ver
        // SentryBootstrap/SentrySettings). El "using" asegura el flush/cierre del transporte al
        // salir de app.Run().
        using var sentry = SentryBootstrap.Init();
        StartupLogger.Log("SentryBootstrap.Init() done.");

        var app = new App();
        StartupLogger.Log("App() constructed (DispatcherUnhandledException/AppDomain handlers now wired).");
        app.InitializeComponent();
        StartupLogger.Log("InitializeComponent() done. Calling app.Run()...");
        app.Run();
        StartupLogger.Log("app.Run() returned — app is shutting down.");
    }

    /// <summary>Versión del assembly EN EJECUCIÓN (memoria), no la que Velopack reporta desde disco.</summary>
    private static string RunningVersionText =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "desconocida";

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupLogger.Log($"OnStartup begin. Running assembly version: {RunningVersionText}.");

        base.OnStartup(e);

        // Sin StartupUri, WPF no infiere más ShutdownMode a partir de él; lo dejamos explícito para
        // que no dependa del default implícito. OnLastWindowClose es el comportamiento correcto acá:
        // "cerrar" la ventana con [x] normalmente hace Hide() (minimizar a bandeja, ver
        // OnMainWindowClosing) en vez de Close(), así que la ventana oculta sigue contando como
        // "abierta" y la app NO se apaga sola al minimizar. Solo se apaga cuando la ventana se cierra
        // de verdad (Salir desde la bandeja) o Shutdown() se llama explícitamente.
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        // Tema claro/oscuro: se aplica ANTES de crear MainWindow (ver más abajo) para que la
        // ventana nazca ya con el tema correcto, sin flash del tema claro por default que trae
        // App.xaml (ver comentario ahí) seguido de un cambio visible a oscuro. ThemeManager.Apply
        // resuelve "Sistema" contra el registro de Windows y reemplaza el ResourceDictionary de
        // colores activo; los consumidores usan DynamicResource así que esto también sirve para
        // cambios posteriores desde SettingsWindow.
        ThemeManager.Apply(ThemeResolver.Parse(AppSettings.Instance.Theme));
        StartupLogger.Log($"Theme applied: setting={ThemeManager.CurrentSetting}, effective={ThemeManager.EffectiveTheme}.");

        // Migrates users who already had "Start with Windows" enabled before --minimized existed
        // (see AutoStartRegistration.MinimizedArg / TryMigrateAutoStartRegistration below) so
        // their NEXT auto-start launch already carries the flag.
        TryMigrateAutoStartRegistration();

        // Bug 2026-07-09: launching via Windows auto-start opened a normal, visible window
        // instead of starting minimized to the tray (as Configuración's copy promises), because
        // nothing distinguished a manual launch from an auto-start one. AutoStartRegistration now
        // registers the exe with the --minimized flag (see there); this reads it back via the pure,
        // GUI-free StartupWindowMode.ShouldStartMinimized so a manual double-click (no flag) keeps
        // showing the window as before.
        var startMinimized = StartupWindowMode.ShouldStartMinimized(_startupArgs);
        StartupLogger.Log($"StartMinimized={startMinimized} (args: [{string.Join(' ', _startupArgs)}]).");

        // App.xaml YA NO tiene StartupUri (bug 2026-07-08 parte 2: con StartupUri, WPF crea/muestra
        // MainWindow de forma no determinística en este setup de arranque custom -- ver Main() más
        // arriba, que corre VelopackApp.Build().Run() + Sentry ANTES de app.Run(). Según el timing,
        // base.OnStartup a veces ya había creado la ventana (MainWindow no null) y a veces no
        // (MainWindow null). El fallback anterior ("si es null, creo una") + el StartupUri corriendo
        // igual producían DOS ventanas cuando el timing daba null tras el fallback ya haberla creado,
        // o CERO cuando ninguno de los dos corría a tiempo (bug 2026-07-08 parte 1, versión 1.0.16).
        // Fix definitivo: sacamos StartupUri del todo y creamos la ventana acá, siempre, una única
        // vez -- sin condicional, sin carrera posible.
        StartupLogger.Log("Creating MainWindow explicitly (no StartupUri).");

        Window? window = null;
        try
        {
            window = new MainWindow();
            MainWindow = window;

            if (startMinimized)
            {
                // Start minimized to the tray without ever showing a visible window, but WITHOUT
                // skipping Show() entirely: WPF only adds a window to Application.Windows once it
                // has been shown (Loaded), and ShutdownMode.OnLastWindowClose (see above) needs at
                // least one window in that collection or the app shuts itself down right after
                // OnStartup returns. This reuses the exact same "minimize to tray" mechanism as
                // OnMainWindowClosing's Hide() below — a hidden-but-shown window counts as "open".
                // WindowState=Minimized + ShowInTaskbar=false BEFORE Show() is extra insurance
                // against a visible flash (the window never gets a normal on-screen paint pass);
                // Hide() right after removes it from the screen/taskbar entirely. Both are reset
                // back to Normal/true immediately after so a later restore from the tray
                // (TrayIconService.OpenApp) shows a normal window again, not a minimized one stuck
                // outside the taskbar.
                window.WindowState = WindowState.Minimized;
                window.ShowInTaskbar = false;
                window.Show();
                window.Hide();
                window.WindowState = WindowState.Normal;
                window.ShowInTaskbar = true;
                StartupLogger.Log("MainWindow created and started minimized to tray (--minimized).");
            }
            else
            {
                window.Show();
                StartupLogger.Log("MainWindow created and shown.");
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex);
            StartupLogger.Log($"MainWindow creation failed: {ex.GetType().FullName}: {ex.Message}");
            window = null;

            // Sin esto, con ShutdownMode.OnLastWindowClose (ver arriba) y ninguna ventana jamás
            // mostrada, el message-loop seguía corriendo para siempre -- y como window queda null,
            // TrayIconService tampoco se crea (ver más abajo), así que quedaba un proceso zombie e
            // invisible, sin forma de cerrarlo salvo Task Manager. Avisamos con un mensaje claro y
            // cerramos el proceso explícitamente.
            MessageBox.Show(
                "No se pudo iniciar la aplicación. Cerrala y volvé a abrirla; si el problema " +
                "sigue, reinstalá desde el último instalador.",
                "Error al iniciar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }

        if (window is not null)
        {
            try
            {
                _trayIconService = new TrayIconService(window);
                StartupLogger.Log("TrayIconService created.");
            }
            catch (Exception ex)
            {
                // Defensa en profundidad (bug 2026-07-08: el ícono de la bandeja no aparece, SIN
                // crash log). El try/catch interno de TrayIconService.LoadTrayIcon ya cubre fallos
                // al cargar el ícono en sí, pero el constructor completo (ForceCreate() -- con su
                // propio try/catch ahora, ver TrayIconService --, SyncCoordinator.Instance,
                // AutoStartHelper.IsAutoStartEnabled() leyendo el registro, etc.) no tenía NINGÚN
                // try/catch acá: si algo ahí tirara, _trayIconService quedaba null en silencio, sin
                // rastro en ningún log. Con esto, la app sigue funcionando sin bandeja (en vez de
                // quedar potencialmente inaccesible al minimizar) y queda registro de la causa.
                CrashLogger.Log(ex);
                TrayIconLogger.Log($"Excepción creando TrayIconService: {ex.GetType().FullName}: {ex.Message}");
                StartupLogger.Log($"TrayIconService creation failed: {ex.GetType().FullName}: {ex.Message}");
            }

            // Close-to-tray behavior — see tray "Salir" menu item for the real exit path.
            // Revert by removing this Closing handler override.
            window.Closing += OnMainWindowClosing;
        }
        else
        {
            StartupLogger.Log("MainWindow creation failed — tray icon NOT created.");
        }

        Exit += OnExit;

        // Chequeo de actualización al abrir: fire-and-forget, best-effort (ver UpdateService),
        // nunca debe bloquear ni retrasar el arranque de la ventana.
        _ = UpdateService.Instance.CheckAndDownloadAsync();

        // Re-chequeo periódico (cada 4hs) mientras la app sigue corriendo: sin esto, el chequeo de
        // arriba era el ÚNICO que corría en toda la vida del proceso -- como la app vive en la
        // bandeja sin reiniciarse (ver ShutdownMode.OnLastWindowClose más arriba), un usuario que la
        // deja abierta días/semanas nunca se enteraba de una versión nueva hasta el próximo
        // relanzamiento manual. Se detiene en OnExit.
        UpdateService.Instance.StartPeriodicChecks();

        StartupLogger.Log("OnStartup end.");
    }

    /// <summary>
    /// Best-effort migration for users who already had "Start with Windows" enabled before the
    /// --minimized flag existed (see AutoStartRegistration.MinimizedArg): re-registering with
    /// AutoStartHelper.SetAutoStart(true) rewrites the HKCU\...\Run value in the new format, so
    /// their NEXT auto-start launch already carries the flag. Safe to call on every startup
    /// (idempotent — rewrites the same value if it already has the flag) and never breaks
    /// startup: AutoStartHelper already swallows registry errors internally, this catch is just
    /// defense in depth, same pattern as the TrayIconService creation try/catch above.
    /// </summary>
    private static void TryMigrateAutoStartRegistration()
    {
        try
        {
            if (AutoStartHelper.IsAutoStartEnabled())
            {
                AutoStartHelper.SetAutoStart(true);
                StartupLogger.Log("Auto-start registration migrated/refreshed to the current format.");
            }
        }
        catch (Exception ex)
        {
            StartupLogger.Log($"Auto-start registration migration failed: {ex.GetType().FullName}: {ex.Message}");
        }
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        // ConsumeExitRequested() (no el getter IsExitRequested a secas) es a propósito: consume el
        // flag de "Salir" de una sola vez por intento de cierre, para que un intento de Shutdown()
        // interrumpido (p.ej. una excepción en Dispose) no deje el próximo cierre por [x] pegado en
        // "cerrar de verdad" para siempre. Ver el comentario en TrayIconService.ConsumeExitRequested.
        var exitRequested = _trayIconService?.ConsumeExitRequested() ?? false;
        var minimizeToTrayOnClose = AppSettings.Instance.MinimizeToTrayOnClose;
        var action = WindowCloseBehavior.Resolve(minimizeToTrayOnClose, exitRequested);

        // Instrumentación de diagnóstico (no un error): registra CADA intento de cierre para poder
        // diagnosticar el próximo reporte del bug "cierra en vez de minimizar" sin depender de
        // reproducirlo en desarrollo. Ver changelog 2026-07-08.
        CloseFlowLogger.Log(minimizeToTrayOnClose, exitRequested, action);

        if (action == WindowCloseAction.Exit)
        {
            // Salida real (pedida desde "Salir" o porque el setting está apagado): liberamos acá
            // (antes de que la ventana termine de cerrarse) grabación/reproducción/watcher/timers
            // en curso. NO se llama al minimizar a la bandeja (ver rama de abajo): ahí la app sigue
            // viva en segundo plano y esos recursos deben seguir funcionando.
            (MainWindow?.DataContext as MainViewModel)?.Dispose();
            return; // dejamos que cierre.
        }

        // En vez de cerrar, minimizamos a la bandeja.
        e.Cancel = true;
        MainWindow?.Hide();
    }

    private void OnExit(object? sender, ExitEventArgs e)
    {
        UpdateService.Instance.StopPeriodicChecks();
        _trayIconService?.Dispose();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        CrashLogger.Log(e.Exception);

        // No-op si Sentry no está habilitado (sin DSN configurado). AppDomain.UnhandledException y
        // TaskScheduler.UnobservedTaskException ya se capturan solos vía las integraciones default
        // de SentrySdk.Init (ver SentryBootstrap) — DispatcherUnhandledException es WPF-específico
        // y necesita este capture manual porque el SDK no lo cubre.
        SentrySdk.CaptureException(e.Exception);

        MessageBox.Show(
            e.Exception.Message,
            "Ocurrió un error inesperado",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Marcamos como manejada para que la app siga viva.
        e.Handled = true;
    }

    /// <summary>
    /// Excepciones no manejadas fuera del Dispatcher de UI (ej. en un thread propio). Sentry ya las
    /// captura solo (integración default); acá solo agregamos el efecto adicional de escribirlas en
    /// el crash log local. Este evento no es cancelable: en la gran mayoría de los casos el proceso
    /// termina igual apenas retorna este handler.
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            CrashLogger.Log(ex);
    }

    /// <summary>
    /// Excepción de una Task cuyo resultado nadie observó (ej. un "async void" o un Task
    /// fire-and-forget que falló). Sentry ya la captura solo; acá solo agregamos el crash log local.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        CrashLogger.Log(e.Exception);

    /// <summary>Persiste el error en %LOCALAPPDATA%\AudioTranscriber\error.log para diagnóstico.</summary>
    private static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioTranscriber");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n";
            File.AppendAllText(Path.Combine(dir, "error.log"), line);
        }
        catch { /* si ni siquiera se puede loguear, no hay más que hacer */ }
    }
}
