using System.Windows;
using AudioTranscriber.App.ViewModels;

namespace AudioTranscriber.App;

/// <summary>
/// "Unir notas" NATIVO: combina varias transcripciones ya elegidas en un solo documento generado
/// por IA (ver <see cref="MergeNotesViewModel"/>, brief "Híbrido nativo" 2026-07-14). Abierta desde
/// "Combinar en documento", la acción del asistente de proyecto (ver
/// <c>BrainViewModel.CombineIntoDocumentCommand</c>, feature 1.0.56) — el viejo modo checkbox de
/// <c>MainWindow</c> que abría esta misma ventana se eliminó en el rediseño 2026-07-22.
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
