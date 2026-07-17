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
}
