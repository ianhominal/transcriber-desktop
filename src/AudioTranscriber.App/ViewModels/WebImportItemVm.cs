using AudioTranscriber.Core.WebImport;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// Un ítem de la lista de resultados de <see cref="WebImportViewModel.AnalyzeCommand"/>: el
/// <see cref="WebMediaItem"/> que trajo yt-dlp más el tilde de selección de la usuaria. La duración
/// ya viene formateada (<see cref="WebImportViewState.FormatDuration"/>, Core) para no repetir esa
/// lógica en XAML con un converter.
/// </summary>
public sealed partial class WebImportItemVm : ObservableObject
{
    private readonly Action? _onSelectionChanged;

    public WebImportItemVm(WebMediaItem item, Action? onSelectionChanged = null)
    {
        Item = item;
        _onSelectionChanged = onSelectionChanged;
    }

    public WebMediaItem Item { get; }

    public string Title => Item.Title;

    public string DurationText => WebImportViewState.FormatDuration(Item.Duration);

    [ObservableProperty]
    private bool _isSelected;

    // CRÍTICO (ver CLAUDE.md): WebImportViewModel.SelectedCount no es un [ObservableProperty] sobre
    // ESTA clase (vive en la lista, no en el ítem), así que no hay [NotifyCanExecuteChangedFor] que
    // aplicar acá -- en cambio, se avisa al padre a mano para que recalcule su propio contador y
    // dispare el CanExecute de ConfirmCommand (ver WebImportViewModel.OnItemSelectionChanged).
    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged?.Invoke();
}
