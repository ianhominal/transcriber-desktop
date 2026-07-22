using System.Windows;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// "Chat con IA" en alcance "Todas mis notas" (ver <see cref="BrainViewModel"/>, brief "Híbrido
/// nativo" 2026-07-14, unificado 2026-07-14 -- ver commit 7f20a43 de la web). Abierta desde
/// <c>MainWindow</c> ("Chat con IA"), mismo criterio de ventana independiente (no modal) que
/// <see cref="FormatosWindow"/>.
/// </summary>
public partial class BrainWindow : Window
{
    private readonly BrainViewModel _viewModel;

    public BrainWindow()
    {
        InitializeComponent();

        _viewModel = new BrainViewModel();
        DataContext = _viewModel;

        // Auto-scroll al fondo del chat cada vez que se agrega un mensaje -- mismo puente MVVM que
        // NoteDetailWindow (un ScrollViewer no tiene forma de "seguir" contenido vía binding puro).
        _viewModel.Messages.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => MessagesScroll.ScrollToEnd());
    }

    /// <summary>
    /// "Asistente del proyecto" (ver <see cref="BrainViewModel"/>, constructor con <c>projectId</c>):
    /// abierta desde <c>MainViewModel.OpenProjectAssistantCommand</c> (nodo de un proyecto en el
    /// árbol). Segunda ventana WPF-generada requiere su propio <c>InitializeComponent()</c> -- no
    /// hay forma de encadenar constructores parciales de XAML entre sí para reusarlo.
    /// </summary>
    public BrainWindow(string projectId, string projectName, IReadOnlyList<(string RemoteId, string Title)> mergeCandidates)
    {
        InitializeComponent();

        _viewModel = new BrainViewModel(projectId, projectName, mergeCandidates);
        DataContext = _viewModel;

        // El TextBlock del contenido ya está bindeado a HeaderTitle, pero el chrome de la ventana
        // (Title del SO/taskbar y el TitleBar custom) es texto plano en el XAML -- sin esto se
        // quedaría mostrando "Chat con IA" también en el alcance de proyecto.
        Title = $"{_viewModel.HeaderTitle} — Audio Transcriber";
        WindowTitleBar.TitleText = _viewModel.HeaderTitle;

        _viewModel.Messages.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => MessagesScroll.ScrollToEnd());
    }
}
