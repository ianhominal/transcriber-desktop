using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Restaura tamaño/posición/estado guardados del último cierre real (o calcula un tamaño
        // inicial proporcional al monitor si es el primer arranque) — ver WindowBoundsPersistence.
        // DEBE correr acá, antes de que App.xaml.cs llame a Show()/el flujo de arranque minimizado
        // a bandeja: WindowStartupLocation pasa a Manual adentro de Apply, así que si esto corriera
        // después de mostrar la ventana ya sería tarde para reposicionarla sin flash visible.
        WindowBoundsPersistence.Apply(this);

        // Sets the window's small icon (Alt-Tab thumbnail, etc.) to match the custom title bar's
        // icon — see AppIconLoader for why this isn't a pack URI. The taskbar/exe icon itself
        // already comes from <ApplicationIcon> in the csproj, so this is purely cosmetic polish.
        Icon = AppIconLoader.Load();

        // Onboarding (login -> elegir carpeta) recién cuando la ventana ya está armada: abrir
        // diálogos desde el constructor (antes de Loaded) es frágil (Owner todavía no asignable).
        Loaded += OnMainWindowLoaded;
    }

    /// <summary>
    /// Compensates the classic WindowChrome + WindowState.Maximized bug: without this, a
    /// maximized window paints a few pixels past the visible work area on each edge (as if it
    /// still had the native resize border), clipping content and overlapping the taskbar.
    /// Adding SystemParameters.WindowResizeBorderThickness as inner margin while maximized —
    /// and removing it again when not — is the standard fix for this.
    /// </summary>
    private void OnMainWindowStateChanged(object sender, EventArgs e)
    {
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : new Thickness(0);
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.EnsureOnboarded();
    }

    // ---- Drag & drop de archivos ----------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;

        if (DataContext is MainViewModel vm)
            vm.AddDroppedFiles(files);
    }

    // ---- Seek de la barra de reproducción -------------------------------

    private void OnSeekStart(object sender, DragStartedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.BeginSeek();
    }

    private void OnSeekEnd(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.EndSeek(PlaybackSlider.Value);
    }

    private void OnSeekClick(object sender, MouseButtonEventArgs e)
    {
        // Clic directo sobre la barra (sin arrastrar): saltar a ese punto.
        if (DataContext is MainViewModel vm)
            vm.EndSeek(PlaybackSlider.Value);
    }

    // ---- Selector de aplicación para "Grabar reunión" --------------------

    // Las aplicaciones que suenan cambian todo el tiempo (una pestaña deja de reproducir, se
    // cierra Spotify...), así que la lista se arma recién al abrir el combo, no una sola vez al
    // arrancar la app -- mismo criterio de forwarding a code-behind que OnTreeSelectionChanged.
    private void OnMeetingAudioSourceDropDownOpened(object sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.RefreshMeetingAudioSourceOptions();
    }

    // ---- Explorador de proyectos ----------------------------------------

    private Point _dragStart;

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel vm)
            vm.OnTreeSelectionChanged(e.NewValue);
    }

    // F3: clic sobre un ítem del listado de archivos de la vista de proyecto (panel derecho, ver
    // MainViewModel.ShowProjectFilesView) -- mismo criterio de forwarding a code-behind que el
    // TreeView de arriba, en vez de un Command bindeado desde el DataTemplate del ítem.
    //
    // Multi-select nativo (SelectionMode="Extended", ver MainWindow.xaml): ListBox.SelectedItems
    // no es bindeable en WPF, así que se trackea acá y se lo pasa al VM
    // (MainViewModel.SetSelectedProjectFiles) para el botón/tecla "Borrar seleccionados". Con
    // exactamente 1 elegido Y sin Ctrl/Shift (Keyboard.Modifiers) se abre la nota, igual que
    // antes -- un click plano SIEMPRE cae en este caso, así que el comportamiento de un solo
    // audio no cambió. Si el 1 restante llegó por Ctrl/Shift-click (por ej. deseleccionando hasta
    // dejar uno solo mientras se arma una selección múltiple) NO se abre la nota: abrir acá
    // dispararía SelectedAudio != null, lo que oculta este mismo ListBox (ver
    // ShowProjectFilesView) y cortaría la selección múltiple a mitad de camino.
    private void OnProjectFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not ListBox listBox)
            return;

        var selected = listBox.SelectedItems.Cast<AudioItemVm>().ToList();
        vm.SetSelectedProjectFiles(selected);

        if (selected.Count == 1 && Keyboard.Modifiers == ModifierKeys.None)
            vm.SelectProjectFile(selected[0]);
    }

    // Tecla Delete sobre ProjectFilesList: borra todo lo seleccionado (1 o más), mismo comando que
    // el botón contextual "Borrar seleccionados (N)" del header.
    private void OnProjectFilesListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not MainViewModel vm)
            return;
        if (vm.DeleteSelectedFilesCommand.CanExecute(null))
        {
            vm.DeleteSelectedFilesCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Iniciar arrastre de un audio (para moverlo a otro proyecto).
    private void OnAudioMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        if (sender is FrameworkElement fe && fe.DataContext is AudioItemVm audio)
            DragDrop.DoDragDrop(fe, new DataObject("audioItem", audio), DragDropEffects.Move);
    }

    private void OnProjectDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("audioItem") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnProjectDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("audioItem") &&
            sender is FrameworkElement fe && fe.DataContext is ProjectVm target &&
            e.Data.GetData("audioItem") is AudioItemVm audio &&
            DataContext is MainViewModel vm)
        {
            vm.MoveAudioToProject(audio, target);
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        _dragStart = e.GetPosition(null);
    }

    // ---- Resultados de búsqueda (2026-07-14) -----------------------------

    // Clic sobre un resultado de búsqueda (panel de la derecha, ver MainViewModel.ShowSearchResults)
    // -- mismo criterio de forwarding a code-behind que OnProjectFileSelectionChanged, en vez de un
    // Command bindeado desde el DataTemplate del ítem (el Border del resultado no es un
    // ListBoxItem, así que no hay SelectionChanged nativo para engancharse).
    private void OnSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is FrameworkElement { DataContext: AiSearchResultVm result })
            vm.OpenSearchResultCommand.Execute(result);
    }
}
