using System.Windows;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// "Unir notas" NATIVO: combina varias transcripciones ya elegidas en <c>MainWindow</c> en un solo
/// documento generado por IA (ver <see cref="MergeNotesViewModel"/>, brief "Híbrido nativo"
/// 2026-07-14). Abierta desde <c>MainViewModel.MergeSelectedCommand</c>.
/// </summary>
public partial class MergeNotesWindow : Window
{
    private readonly MergeNotesViewModel _viewModel;

    /// <param name="notes">Notas elegidas para unir: id remoto (Supabase) + título, ya validadas
    /// (2 a 20, todas sincronizadas) por el caller.</param>
    public MergeNotesWindow(IReadOnlyList<(string RemoteId, string Title)> notes)
    {
        InitializeComponent();

        _viewModel = new MergeNotesViewModel(notes);
        DataContext = _viewModel;
    }
}
