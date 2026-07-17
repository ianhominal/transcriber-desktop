using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using AudioTranscriber.Core.Observability;
using WinForms = System.Windows.Forms;

namespace AudioTranscriber.App;

/// <summary>
/// System tray icon with context menu: sync status, shortcuts to the sync folder and the app,
/// manual sync, Windows auto-start, and the real exit path. Created once from
/// <see cref="App.OnStartup"/> and lives for as long as the app is running.
/// <para/>
/// Uses <see cref="WinForms.NotifyIcon"/> (classic WinForms tray icon) instead of a WPF-native
/// library. Reason: this app previously used H.NotifyIcon.Wpf 2.3.2, and its <c>TaskbarIcon</c>
/// never became visible on the user's Windows 10 (build 19045), even with <c>ForceCreate()</c>
/// and the "refresh nudge" workaround for
/// https://github.com/HavenDV/H.NotifyIcon/issues/14 — confirmed empirically across every version
/// up to 1.0.15. <see cref="WinForms.NotifyIcon"/> registers the icon synchronously via
/// Shell_NotifyIcon and does not share that repaint bug, so it is the more robust choice here.
/// WinForms and WPF can coexist on the same STA thread: WPF's <see cref="System.Windows.Threading.Dispatcher"/>
/// already pumps the thread's Win32 message queue, which is exactly what
/// <see cref="WinForms.NotifyIcon"/>'s hidden message-only window needs to receive its
/// notifications — there is no need to call <c>System.Windows.Forms.Application.Run()</c>.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Window _mainWindow;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _contextMenuStrip;
    private readonly WinForms.ToolStripMenuItem _statusMenuItem;
    private readonly WinForms.ToolStripMenuItem _autoStartMenuItem;
    private readonly WinForms.ToolStripMenuItem _updateMenuItem;
    private readonly Icon? _icon;

    /// <summary>
    /// Seam sobre la notificación tipo globo de la bandeja (ver <see cref="ITrayNotifier"/>). En
    /// producción es el <see cref="NotifyIconTrayNotifier"/> que envuelve al
    /// <see cref="WinForms.NotifyIcon"/> real; en tests se inyecta un fake para poder verificar que
    /// "se pidió una notificación" SIN que WinForms dispare un globo real (sonido + toast) en la
    /// máquina de quien corre los tests.
    /// </summary>
    private readonly ITrayNotifier _notifier;

    /// <summary>
    /// True when the real shutdown was requested from the "Exit" menu item (not from the window's
    /// [x] button). Read through <see cref="ConsumeExitRequested"/> from App.xaml.cs, NOT directly:
    /// this public getter exists only for inspection/tests, so existing callers that read it that
    /// way keep working.
    /// </summary>
    public bool IsExitRequested { get; private set; }

    /// <summary>
    /// Reads <see cref="IsExitRequested"/> and resets it to false in the same step (consumed once
    /// per close attempt). Real bug that motivated this: it used to be a latch that, once set to
    /// true from "Exit", stayed stuck forever if <c>Application.Shutdown()</c> was interrupted
    /// halfway through (e.g. an exception during <c>MainViewModel.Dispose()</c> in
    /// OnMainWindowClosing, caught by DispatcherUnhandledException, which leaves the app alive) —
    /// from that point on, ANY later click on [x] closed the app for real regardless of the
    /// "Minimize to tray on close" toggle, because WindowCloseBehavior.Resolve kept seeing
    /// exitRequested=true from a previous "Exit" attempt that never completed. A clean close, no new
    /// exception or crash log — matches what was reported. Consuming the flag here guarantees every
    /// close attempt starts from a clean slate. See changelog 2026-07-08.
    /// </summary>
    public bool ConsumeExitRequested()
    {
        var value = IsExitRequested;
        IsExitRequested = false;
        return value;
    }

    /// <summary>
    /// Single instance while the app runs (created once from App.OnStartup). Lets SettingsWindow,
    /// which can also change the "Start with Windows" state, ask this tray menu item to
    /// resynchronize itself against the registry when it closes (see
    /// <see cref="RefreshAutoStartState"/>) without having to thread the instance through everywhere.
    /// </summary>
    public static TrayIconService? Current { get; private set; }

    /// <param name="notifier">
    /// Inyectable SOLO para tests (default = notificador real sobre el <see cref="WinForms.NotifyIcon"/>
    /// de la bandeja). Permite verificar el flujo de "actualización lista" sin disparar un globo real
    /// en la máquina. En producción se llama siempre con este parámetro en null.
    /// </param>
    public TrayIconService(Window mainWindow, ITrayNotifier? notifier = null)
    {
        Current = this;
        _mainWindow = mainWindow;
        SyncCoordinator.Instance.PropertyChanged += OnSyncCoordinatorPropertyChanged;
        UpdateService.Instance.UpdateReady += OnUpdateReady;

        _statusMenuItem = new WinForms.ToolStripMenuItem { Enabled = false };

        // Hidden until UpdateService reports a downloaded, ready-to-apply update (see
        // OnUpdateReady). Never applied automatically: always requires this explicit click.
        _updateMenuItem = new WinForms.ToolStripMenuItem("Reiniciar y actualizar") { Visible = false };
        _updateMenuItem.Click += (_, _) => UpdateService.Instance.ApplyAndRestart();

        // CheckOnClick makes the item toggle Checked BEFORE Click fires (same behavior the old
        // WPF MenuItem had), so OnToggleAutoStart only needs to persist the new state.
        _autoStartMenuItem = new WinForms.ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true };
        // Synchronize the check against the registry (source of truth), not just the value
        // mirrored in AppSettings.
        var autoStartEnabled = AutoStartHelper.IsAutoStartEnabled();
        _autoStartMenuItem.Checked = autoStartEnabled;
        PersistAutoStartMirror(autoStartEnabled);
        _autoStartMenuItem.Click += OnToggleAutoStart;

        var openAppItem = new WinForms.ToolStripMenuItem("Abrir app");
        openAppItem.Click += (_, _) => OpenApp();

        var openFolderItem = new WinForms.ToolStripMenuItem("Abrir carpeta");
        openFolderItem.Click += (_, _) => OpenSyncFolder();

        var syncNowItem = new WinForms.ToolStripMenuItem("Sincronizar ahora");
        syncNowItem.Click += (_, _) => SyncCoordinator.Instance.SyncNowCommand.Execute(null);

        // Different from "openFolderItem" above (which opens the SYNC folder): this opens the
        // local crash log folder (see CrashLogger), so the user can send the file without needing
        // screenshots.
        var openLogsFolderItem = new WinForms.ToolStripMenuItem("Abrir carpeta de logs");
        openLogsFolderItem.Click += (_, _) => OpenLogsFolder();

        var settingsItem = new WinForms.ToolStripMenuItem("Configuración");
        settingsItem.Click += (_, _) => OpenSettings();

        var exitItem = new WinForms.ToolStripMenuItem("Salir");
        exitItem.Click += (_, _) => RequestExit();

        _contextMenuStrip = new WinForms.ContextMenuStrip();
        _contextMenuStrip.Items.Add(_statusMenuItem);
        _contextMenuStrip.Items.Add(_updateMenuItem);
        _contextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        _contextMenuStrip.Items.Add(openAppItem);
        _contextMenuStrip.Items.Add(openFolderItem);
        _contextMenuStrip.Items.Add(syncNowItem);
        _contextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        _contextMenuStrip.Items.Add(_autoStartMenuItem);
        _contextMenuStrip.Items.Add(openLogsFolderItem);
        _contextMenuStrip.Items.Add(settingsItem);
        _contextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        _contextMenuStrip.Items.Add(exitItem);

        _icon = LoadTrayIcon();

        _notifyIcon = new WinForms.NotifyIcon
        {
            // NotifyIcon requires a non-null Icon before Visible can be set to true, unlike
            // H.NotifyIcon.Wpf's TaskbarIcon (which tolerated a null Icon). Falling back to a
            // built-in system icon here keeps the tray icon itself from ever being the reason the
            // app fails to start; LoadTrayIcon() already logs whether the embedded resource load
            // succeeded or not.
            Icon = _icon ?? SystemIcons.Application,
            Text = "Audio Transcriber", // tooltip, must stay under NotifyIcon's 63-char limit
            ContextMenuStrip = _contextMenuStrip,
        };
        _notifyIcon.MouseDoubleClick += OnTrayIconMouseDoubleClick;

        // Por default, el notificador real envuelve al NotifyIcon recién creado; un test puede pasar
        // un fake por el constructor para no disparar el globo real (ver ITrayNotifier).
        _notifier = notifier ?? new NotifyIconTrayNotifier(_notifyIcon);

        try
        {
            _notifyIcon.Visible = true;
            TrayIconLogger.Log(
                $"NotifyIcon.Visible=true set without exception. UsingEmbeddedIcon={_icon is not null}.");
        }
        catch (Exception ex)
        {
            // Defensive instrumentation kept from the previous implementation (bug 2026-07-08: the
            // tray icon never appeared, with no crash log). If setting Visible=true throws for any
            // reason, it gets logged here.
            TrayIconLogger.Log($"Setting NotifyIcon.Visible=true threw: {ex.GetType().FullName}: {ex.Message}");
            CrashLogger.Log(ex);
        }

        UpdateStatusHeader();
    }

    private void OnTrayIconMouseDoubleClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
            OpenApp();
    }

    private void OnSyncCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        UpdateStatusHeader();

    private void UpdateStatusHeader()
    {
        // OnSyncCoordinatorPropertyChanged can fire from a background thread (sync runs async).
        // _statusMenuItem is a WinForms ToolStripItem, not a WPF DispatcherObject, so it has no
        // Dispatcher of its own — _mainWindow.Dispatcher is used instead as the single reference
        // to "the UI thread" that also owns the NotifyIcon and its menu (both created from
        // App.OnStartup on that same thread). Keeps the same defensive marshalling that fixed a
        // real crash in v1.0.6 ("the calling thread cannot access this object").
        if (_mainWindow.Dispatcher.CheckAccess())
            _statusMenuItem.Text = SyncCoordinator.Instance.DisplayStatus;
        else
            _mainWindow.Dispatcher.BeginInvoke(
                () => _statusMenuItem.Text = SyncCoordinator.Instance.DisplayStatus);
    }

    /// <summary>
    /// UpdateService already downloaded a new version: show the "Reiniciar y actualizar" tray menu
    /// item and a balloon notification so the user finds out without having to open the menu. The
    /// restart is NEVER automatic, only from this explicit click.
    /// </summary>
    private void OnUpdateReady(string newVersion)
    {
        // UpdateService.UpdateReady can fire from a background thread (CheckAndDownloadAsync):
        // _updateMenuItem and the balloon notification are UI-affine, so they must be touched on
        // the UI thread.
        if (!_mainWindow.Dispatcher.CheckAccess())
        {
            _mainWindow.Dispatcher.BeginInvoke(() => OnUpdateReady(newVersion));
            return;
        }
        _updateMenuItem.Visible = true;
        _notifier.ShowBalloon(
            "Actualización disponible",
            $"Audio Transcriber {newVersion} está lista. Click en 'Reiniciar y actualizar' en el ícono de la bandeja para aplicarla.");
    }

    private void OpenApp()
    {
        void Restore()
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        // NotifyIcon events are expected to already fire on the WPF UI thread (WPF's Dispatcher
        // pumps the same Win32 message queue that delivers the notify-icon window's messages), but
        // this stays defensive and marshals explicitly, same as UpdateStatusHeader/OnUpdateReady.
        if (_mainWindow.Dispatcher.CheckAccess())
            Restore();
        else
            _mainWindow.Dispatcher.Invoke(Restore);
    }

    private void RequestExit()
    {
        void Shutdown()
        {
            IsExitRequested = true;
            Application.Current.Shutdown();
        }

        if (Application.Current.Dispatcher.CheckAccess())
            Shutdown();
        else
            Application.Current.Dispatcher.Invoke(Shutdown);
    }

    private void OpenSyncFolder()
    {
        var path = SyncCoordinator.Instance.SyncFolder;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(
                "Todavía no configuraste una carpeta de sincronización.",
                "Audio Transcriber",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Process.Start("explorer.exe", $"\"{path}\"");
    }

    /// <summary>Opens %LOCALAPPDATA%\AudioTranscriber\logs\ (creates the folder if missing).</summary>
    private static void OpenLogsFolder()
    {
        var dir = CrashLogger.EnsureLogDirectory();
        Process.Start("explorer.exe", $"\"{dir}\"");
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        // CheckOnClick already toggled Checked before this handler ran: only the new state needs
        // to be persisted here.
        var enabled = _autoStartMenuItem.Checked;
        AutoStartHelper.SetAutoStart(enabled);
        PersistAutoStartMirror(enabled);
    }

    /// <summary>
    /// Resynchronizes the "Start with Windows" menu item check against the real registry value.
    /// Used after SettingsWindow closes, since it can have changed the state on its own.
    /// </summary>
    public void RefreshAutoStartState()
    {
        var enabled = AutoStartHelper.IsAutoStartEnabled();
        _autoStartMenuItem.Checked = enabled;
        PersistAutoStartMirror(enabled);
    }

    private void OpenSettings()
    {
        OpenApp();
        var window = new SettingsWindow { Owner = _mainWindow };
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Loads the tray icon WITHOUT a pack URI (pack://application:,,,/appicon.ico). Real bug that
    /// motivated this method: that pack URI form depends on Assembly.Location, which WPF needs to
    /// resolve the resource inside the assembly — and Assembly.Location is EMPTY when the app is
    /// published with PublishSingleFile=true (this app's actual publish mode, see changelog), so it
    /// crashed on startup of the installed app with "No se encuentra el recurso 'appicon.ico'."
    /// (captured in a real logs/crash-*.log from an installed build). The fix: read the .ico as a
    /// plain .NET EmbeddedResource (see csproj, LogicalName "AppIcon.ico") via
    /// Assembly.GetManifestResourceStream, which does NOT depend on Location and works the same in
    /// single-file publishes.
    /// <para/>
    /// Must never be able to crash the app: if loading fails for any reason, it is logged to the
    /// local crash log and the caller falls back to a built-in system icon instead of tearing down
    /// startup.
    /// </summary>
    private static Icon? LoadTrayIcon()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("AppIcon.ico");
            if (stream is null)
            {
                // Diagnostic explicitly requested: if the LogicalName ("AppIcon.ico", see csproj)
                // didn't match the real embedded resource (e.g. a build where the name ends up
                // namespaced), stream comes back null HERE, with no exception — exactly the
                // reported symptom (tray with no icon, no crash log). Real resource names are
                // listed so this can be diagnosed without reproducing it by hand on the user's PC.
                var resourceNames = string.Join(", ", assembly.GetManifestResourceNames());
                TrayIconLogger.Log($"AppIcon.ico NOT found as an embedded resource. Available resources: [{resourceNames}]");
                return null;
            }

            TrayIconLogger.Log($"AppIcon.ico found as an embedded resource ({stream.Length} bytes).");
            return new Icon(stream);
        }
        catch (Exception ex)
        {
            TrayIconLogger.Log($"Exception loading the tray icon: {ex.GetType().FullName}: {ex.Message}");
            CrashLogger.Log(ex);
            return null; // The tray icon is still created, just with the system fallback icon.
        }
    }

    private static void PersistAutoStartMirror(bool enabled)
    {
        AppSettings.Instance.AutoStartEnabled = enabled;
        AppSettings.Instance.Save();
    }

    public void Dispose()
    {
        SyncCoordinator.Instance.PropertyChanged -= OnSyncCoordinatorPropertyChanged;
        UpdateService.Instance.UpdateReady -= OnUpdateReady;
        _notifyIcon.MouseDoubleClick -= OnTrayIconMouseDoubleClick;

        // Visible = false before Dispose() is what actually removes the icon from the tray
        // immediately; without it, a ghost icon can linger until the mouse passes over its slot.
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenuStrip.Dispose();
        _icon?.Dispose();

        if (Current == this)
            Current = null;
    }
}

/// <summary>
/// Seam sobre la notificación tipo globo (toast) de la bandeja. Existe solo para poder testear el
/// flujo de "actualización lista" SIN que WinForms dispare una notificación real (sonido + globo)
/// en la máquina de quien corre los tests -- un efecto secundario audible/visible en un test es un
/// bug de calidad. La firma no expone tipos de WinForms a propósito: el timeout y el ícono son
/// detalle de la implementación real (<see cref="NotifyIconTrayNotifier"/>).
/// </summary>
public interface ITrayNotifier
{
    void ShowBalloon(string title, string message);
}

/// <summary>
/// Implementación de producción de <see cref="ITrayNotifier"/>: delega en el
/// <see cref="WinForms.NotifyIcon"/> real de la bandeja, preservando exactamente el mismo timeout
/// (5s) e ícono (Info) que se usaban antes de introducir el seam.
/// </summary>
internal sealed class NotifyIconTrayNotifier : ITrayNotifier
{
    private readonly WinForms.NotifyIcon _notifyIcon;

    public NotifyIconTrayNotifier(WinForms.NotifyIcon notifyIcon) => _notifyIcon = notifyIcon;

    public void ShowBalloon(string title, string message) =>
        _notifyIcon.ShowBalloonTip(5000, title, message, WinForms.ToolTipIcon.Info);
}
