using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AudioTranscriber.App.ViewModels;
using AudioTranscriber.Core.Runtime;
using AudioTranscriber.Core.Sync;
using AudioTranscriber.Core.Updates;

namespace AudioTranscriber.App;

/// <summary>
/// Ventana de configuración: cuenta, carpeta sincronizada y comportamiento (minimizar a la
/// bandeja al cerrar / iniciar con Windows). Accesible desde MainWindow y desde el menú de la
/// bandeja (ver TrayIconService.OpenSettings).
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Misma instancia compartida que MainWindow/SyncWindow/tray: cuenta y carpeta quedan
        // consistentes con el resto de la app (ver comentario en SyncCoordinator.cs).
        DataContext = SyncCoordinator.Instance;

        // "Minimizar a la bandeja": el espejo en AppSettings ES la fuente de verdad (no hay
        // registro externo que consultar, a diferencia del auto-start).
        MinimizeToTrayToggle.IsChecked = AppSettings.Instance.MinimizeToTrayOnClose;

        // "Iniciar con Windows": la fuente de verdad real es el registro (mismo criterio que usa
        // TrayIconService para su propio checkbox), no el espejo en AppSettings.
        AutoStartToggle.IsChecked = AutoStartHelper.IsAutoStartEnabled();

        // "Tema": el índice de ThemeCombo coincide 1:1 con el orden de AppTheme (Light=0, Dark=1,
        // System=2, ver ese enum) y con el orden de los ComboBoxItem del XAML.
        ThemeCombo.SelectedIndex = (int)ThemeResolver.Parse(AppSettings.Instance.Theme);

        // "Transcripción" (F4): valor inicial ANTES de suscribir SelectionChanged, mismo criterio
        // que ThemeCombo arriba (evita un guardado redundante al abrir la ventana).
        EngineCombo.SelectedValue = AppSettings.Instance.Engine;
        QualityCombo.SelectedValue = AppSettings.Instance.GroqModel;
        LanguageCombo.SelectedValue = AppSettings.Instance.Language;
        ModoCombo.SelectedValue = AppSettings.Instance.TranscribeMode;
        TargetLanguageCombo.SelectedValue = AppSettings.Instance.TranslationTargetLanguage;
        UpdateQualityRowVisibility();

        // "Identificar quién habla": valor inicial ANTES de suscribir Checked/Unchecked, mismo
        // criterio que el resto de esta ventana (evita un guardado redundante al abrir).
        UseDiarizationToggle.IsChecked = AppSettings.Instance.UseDiarization;
        UpdateEngineRowEnabled();

        MinimizeToTrayToggle.Checked += OnMinimizeToTrayToggleChanged;
        MinimizeToTrayToggle.Unchecked += OnMinimizeToTrayToggleChanged;
        AutoStartToggle.Checked += OnAutoStartToggleChanged;
        AutoStartToggle.Unchecked += OnAutoStartToggleChanged;
        ThemeCombo.SelectionChanged += OnThemeSelectionChanged;
        EngineCombo.SelectionChanged += OnEngineSelectionChanged;
        QualityCombo.SelectionChanged += OnQualitySelectionChanged;
        LanguageCombo.SelectionChanged += OnLanguageSelectionChanged;
        ModoCombo.SelectionChanged += OnModoSelectionChanged;
        TargetLanguageCombo.SelectionChanged += OnTargetLanguageSelectionChanged;
        UseDiarizationToggle.Checked += OnUseDiarizationToggleChanged;
        UseDiarizationToggle.Unchecked += OnUseDiarizationToggleChanged;

        // Al cerrar, resincronizamos el checkbox del menú de la bandeja por si el usuario cambió
        // el auto-start acá (si no, quedaría desactualizado hasta el próximo restart).
        Closed += (_, _) => TrayIconService.Current?.RefreshAutoStartState();

        CurrentVersionText.Text = UpdateUiTextFormatter.FormatCurrentVersion(ReadAssemblyVersion());

        // Estado PASIVO del updater (pedido: "que el usuario VEA que el updater funciona", sin
        // tener que apretar "Buscar actualizaciones" primero): se inicializa con el ÚLTIMO
        // resultado conocido de CUALQUIER chequeo -- el automático al abrir la app, el periódico en
        // background (ver UpdateService.StartPeriodicChecks), o un manual anterior -- en vez de
        // arrancar en blanco como antes. Si todavía no terminó ninguno (UpdateService.LastResult
        // null), FormatPassiveStatus cae al mismo texto "Buscando actualizaciones…".
        RenderPassiveStatus(UpdateService.Instance.LastResult);

        // Mientras la ventana sigue abierta, un chequeo automático/periódico que termine en
        // background debe reflejarse acá sin que el usuario tenga que cerrar y reabrir Configuración
        // -- mismo criterio de "no quedar desactualizado" que ya usa RefreshAutoStartState arriba.
        UpdateService.Instance.CheckCompleted += OnUpdateCheckCompleted;
        Closed += (_, _) => UpdateService.Instance.CheckCompleted -= OnUpdateCheckCompleted;
    }

    /// <summary>
    /// UpdateService.CheckCompleted puede dispararse desde el timer periódico o el chequeo
    /// automático de arranque: se marshalea al Dispatcher por las dudas, mismo criterio defensivo
    /// que TrayIconService.OnUpdateReady/MainViewModel.OnUpdateReady.
    /// </summary>
    private void OnUpdateCheckCompleted(UpdateCheckResult result)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnUpdateCheckCompleted(result));
            return;
        }
        RenderPassiveStatus(result);
    }

    private void RenderPassiveStatus(UpdateCheckResult? result)
    {
        UpdateStatusText.Text = UpdateUiTextFormatter.FormatPassiveStatus(result);
        RestartNowButton.Visibility = UpdateUiTextFormatter.ShouldShowRestartButton(result)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Versión "Major.Minor.Build" del assembly en ejecución (ej. "1.0.10"), la que MSBuild deriva
    /// de <c>&lt;Version&gt;</c> en el .csproj. Se ignora Revision (siempre 0 acá) para que coincida
    /// con lo que el usuario espera ver, tal cual el csproj.
    /// </summary>
    private static string ReadAssemblyVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// Chequeo manual de actualizaciones (patrón "Check for updates" tipo VS Code/Discord): pide el
    /// resultado a <see cref="UpdateService.CheckForUpdateManualAsync"/> y lo traduce a texto con
    /// <see cref="UpdateUiTextFormatter"/>. No hace falta marshalear al Dispatcher acá (a diferencia
    /// de <see cref="TrayIconService.OnUpdateReady"/>): esto arranca desde un click de UI, así que
    /// la continuación del await vuelve sola al hilo de UI (SynchronizationContext de WPF).
    /// </summary>
    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        RestartNowButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = UpdateUiTextFormatter.CheckingText;

        var result = await UpdateService.Instance.CheckForUpdateManualAsync();

        UpdateStatusText.Text = UpdateUiTextFormatter.FormatResult(result);
        RestartNowButton.Visibility = UpdateUiTextFormatter.ShouldShowRestartButton(result)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CheckUpdatesButton.IsEnabled = true;
    }

    private void OnRestartNowClick(object sender, RoutedEventArgs e) => UpdateService.Instance.ApplyAndRestart();

    private void OnMinimizeToTrayToggleChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.MinimizeToTrayOnClose = MinimizeToTrayToggle.IsChecked == true;
        AppSettings.Instance.Save();
    }

    private void OnAutoStartToggleChanged(object sender, RoutedEventArgs e)
    {
        var enabled = AutoStartToggle.IsChecked == true;
        AutoStartHelper.SetAutoStart(enabled);
        AppSettings.Instance.AutoStartEnabled = enabled;
        AppSettings.Instance.Save();
    }

    /// <summary>
    /// El índice del ComboBox coincide 1:1 con AppTheme (ver comentario en el constructor), así que
    /// castear directo evita depender del Tag de cada ComboBoxItem. Persiste y aplica de una: el
    /// cambio se ve en vivo (ThemeManager reemplaza el ResourceDictionary de colores, y todos los
    /// consumidores usan DynamicResource).
    /// </summary>
    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var theme = (AppTheme)ThemeCombo.SelectedIndex;
        AppSettings.Instance.Theme = theme.ToString();
        AppSettings.Instance.Save();
        ThemeManager.Apply(theme);
    }

    // ---- Transcripción (F4): Motor / Calidad / Idioma, mudados de la barra superior de MainWindow ----

    /// <summary>
    /// Aplica el motor elegido. Si MainWindow ya está abierta (siempre lo está: SettingsWindow se
    /// abre DESDE ahí o desde la bandeja, pero MainWindow nunca se destruye, a lo sumo se
    /// minimiza), escribe directo en MainViewModel.Engine -- su setter generado (ver
    /// OnEngineChanged) ya persiste en AppSettings.Instance.Save() porque es la MISMA instancia
    /// compartida (ver comentario en AppSettings.cs). Así, si la ventana principal queda abierta
    /// mientras se cambia acá, la próxima transcripción usa el valor nuevo sin necesitar un
    /// mecanismo de notificación aparte (AppSettings no es ObservableObject). El fallback directo a
    /// AppSettings cubre el caso defensivo de que MainWindow no esté disponible.
    /// </summary>
    private void OnEngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = EngineCombo.SelectedValue as string ?? AppSettings.Instance.Engine;
        if (Application.Current.MainWindow?.DataContext is MainViewModel vm)
            vm.Engine = value;
        else
        {
            AppSettings.Instance.Engine = value;
            AppSettings.Instance.Save();
        }
        UpdateQualityRowVisibility();
    }

    private void OnQualitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = QualityCombo.SelectedValue as string ?? AppSettings.Instance.GroqModel;
        if (Application.Current.MainWindow?.DataContext is MainViewModel vm)
            vm.GroqModel = value;
        else
        {
            AppSettings.Instance.GroqModel = value;
            AppSettings.Instance.Save();
        }
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = LanguageCombo.SelectedValue as string ?? AppSettings.Instance.Language;
        if (Application.Current.MainWindow?.DataContext is MainViewModel vm)
            vm.Language = value;
        else
        {
            AppSettings.Instance.Language = value;
            AppSettings.Instance.Save();
        }
    }

    /// <summary>"Modo" (Transcribir / Transcribir y traducir, 2026-07-14) -- mismo criterio de
    /// escritura que Motor/Calidad/Idioma arriba: si MainWindow está abierta, escribe directo en
    /// MainViewModel (su setter ya persiste en AppSettings, misma instancia compartida); si no,
    /// escribe el fallback directo a AppSettings.</summary>
    private void OnModoSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = ModoCombo.SelectedValue as string ?? AppSettings.Instance.TranscribeMode;
        if (Application.Current.MainWindow?.DataContext is MainViewModel vm)
            vm.TranscribeMode = value;
        else
        {
            AppSettings.Instance.TranscribeMode = value;
            AppSettings.Instance.Save();
        }
        UpdateQualityRowVisibility();
    }

    private void OnTargetLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var value = TargetLanguageCombo.SelectedValue as string ?? AppSettings.Instance.TranslationTargetLanguage;
        if (Application.Current.MainWindow?.DataContext is MainViewModel vm)
            vm.TranslationTargetLanguage = value;
        else
        {
            AppSettings.Instance.TranslationTargetLanguage = value;
            AppSettings.Instance.Save();
        }
    }

    /// <summary>"Calidad"/"Modo" solo tienen sentido con el motor nube (Groq) -- mismo criterio que
    /// MainViewModel.IsGroq, replicado a mano porque esta ventana no tiene ese binding directo.
    /// "Idioma destino" además depende del Modo elegido (solo con "translate").</summary>
    private void UpdateQualityRowVisibility()
    {
        var engine = EngineCombo.SelectedValue as string ?? AppSettings.Instance.Engine;
        var isGroq = string.Equals(engine, "groq", StringComparison.OrdinalIgnoreCase);
        QualityRow.Visibility = isGroq ? Visibility.Visible : Visibility.Collapsed;
        ModoRow.Visibility = isGroq ? Visibility.Visible : Visibility.Collapsed;

        var modo = ModoCombo.SelectedValue as string ?? AppSettings.Instance.TranscribeMode;
        var isTranslate = string.Equals(modo, "translate", StringComparison.OrdinalIgnoreCase);
        TargetLanguageRow.Visibility = isGroq && isTranslate ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// "Identificar quién habla": mismo criterio de escritura que Motor/Calidad/Idioma arriba (si
    /// MainWindow está abierta, escribe directo en MainViewModel -- su setter ya fuerza el motor a
    /// Local y persiste, ver OnUseDiarizationChanged; si no, fallback directo a AppSettings).
    /// </summary>
    private void OnUseDiarizationToggleChanged(object sender, RoutedEventArgs e)
    {
        var value = UseDiarizationToggle.IsChecked == true;
        if (Application.Current.MainWindow?.DataContext is MainViewModel vm)
            vm.UseDiarization = value;
        else
        {
            AppSettings.Instance.UseDiarization = value;
            AppSettings.Instance.Save();
        }
        UpdateEngineRowEnabled();
    }

    /// <summary>
    /// Con "Identificar quién habla" activo, el motor queda fijo en Local (la nube no identifica
    /// hablantes) -- MainViewModel.OnUseDiarizationChanged ya fuerza Engine="local"; acá se
    /// refleja en esta ventana: se bloquea el selector (mismo bloqueo + tooltip que ya tiene el de
    /// MainWindow, ver IsEngineSelectable/EngineLockedReason) Y se refresca su valor mostrado a
    /// "local" -- si no, alguien que tenía Groq elegido vería el combo apagado pero mostrando
    /// "Groq" igual, con "Calidad"/"Modo" (filas SOLO-Groq) todavía visibles. Reasignar
    /// SelectedValue dispara OnEngineSelectionChanged solo, que ya llama a
    /// UpdateQualityRowVisibility -- no hace falta llamarla acá de nuevo.
    /// </summary>
    private void UpdateEngineRowEnabled()
    {
        var useDiarization = UseDiarizationToggle.IsChecked == true;
        EngineCombo.IsEnabled = !useDiarization;
        ToolTipService.SetShowOnDisabled(EngineCombo, true);
        EngineCombo.ToolTip = useDiarization
            ? "Con \"Identificar quién habla\" activado, el motor queda fijo en Local: la nube (Groq) no puede identificar hablantes."
            : null;

        if (useDiarization)
            EngineCombo.SelectedValue = "local";
    }

    /// <summary>
    /// Reusa el ÚNICO selector de carpeta de toda la app (MainViewModel.OpenWorkspaceCommand):
    /// además de fijar la carpeta en SyncCoordinator, recarga el árbol de proyectos en
    /// MainWindow. No duplicar este picker (ver comentario en SyncWindow.xaml).
    /// </summary>
    private void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow?.DataContext is MainViewModel vm
            && vm.OpenWorkspaceCommand.CanExecute(null))
        {
            vm.OpenWorkspaceCommand.Execute(null);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Abre la gestión NATIVA de vocabulario (ver <see cref="VocabularyWindow"/>).</summary>
    private void OnOpenVocabularyClick(object sender, RoutedEventArgs e) =>
        new VocabularyWindow { Owner = this }.ShowDialog();

    /// <summary>Abre la gestión NATIVA de formatos (ver <see cref="FormatosWindow"/>).</summary>
    private void OnOpenFormatosClick(object sender, RoutedEventArgs e) =>
        new FormatosWindow { Owner = this }.ShowDialog();

    /// <summary>
    /// El desktop no maneja Drive directamente (se configura en la web, ver comentario de la
    /// sección "Google Drive" en el XAML): esto solo abre esa pantalla en el navegador default.
    /// Envuelto en try/catch para no romper la app si no hay navegador default configurado o
    /// falla el shell execute (patrón estándar WPF para abrir URLs con UseShellExecute).
    /// </summary>
    private void OnConfigureGoogleDriveClick(object sender, RoutedEventArgs e)
    {
        var url = GoogleDriveLinkBuilder.BuildSettingsUrl(SyncConfig.BackendBaseUrl);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Best-effort: si falla (sin navegador default, etc.) no interrumpimos al usuario.
        }
    }
}
