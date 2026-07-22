using System.Net.Http;
using AudioTranscriber.Core.Export;
using AudioTranscriber.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// ViewModel de "Unir notas" (<see cref="MergeNotesWindow"/>): combina varias transcripciones ya
/// elegidas (ver <c>BrainViewModel.CombineIntoDocumentCommand</c>, feature 1.0.56) en un solo
/// documento generado por IA (ver <see cref="AiMergeClient"/>, brief "Híbrido nativo" 2026-07-14).
/// Helpers REPLICADOS (no heredados), mismo criterio que <see cref="FormatosViewModel"/>.
/// </summary>
public partial class MergeNotesViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _transcriptionIds;

    /// <summary>Títulos de las notas elegidas, solo para mostrar la lista en la ventana.</summary>
    public IReadOnlyList<string> NoteTitles { get; }

    [ObservableProperty]
    private string _instruction = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyResultCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveAsNoteCommand))]
    private string _resultText = string.Empty;

    /// <summary>Aviso de truncamiento (notas muy largas, ver <see cref="AiMergeResult.Truncated"/>). Vacío = sin aviso.</summary>
    [ObservableProperty]
    private string _truncatedWarning = string.Empty;

    [ObservableProperty]
    private string _operationStatusText = string.Empty;

    public MergeNotesViewModel(IReadOnlyList<(string RemoteId, string Title)> notes)
    {
        _transcriptionIds = notes.Select(n => n.RemoteId).ToList();
        NoteTitles = notes.Select(n => n.Title).ToList();
    }

    private static async Task<string> GetAccessTokenOrThrowAsync()
    {
        var token = await SyncCoordinator.Instance.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Iniciá sesión desde 'Sincronización' para unir notas.");
        return token;
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        AiAssistException or InvalidOperationException => ex.Message,
        HttpRequestException => "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.",
        _ => $"Ocurrió un error inesperado: {ex.Message}",
    };

    private bool CanMerge() => !IsBusy;

    /// <summary>
    /// Une las notas elegidas, con <see cref="Instruction"/> opcional (ej. "armá una minuta con
    /// decisiones y próximos pasos"). El backend responde en streaming de texto plano -- mismo
    /// criterio de <see cref="IProgress{T}"/> acumulado que <c>NoteDetailViewModel.ApplyRecipeAsync</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        ErrorMessage = string.Empty;
        ResultText = string.Empty;
        TruncatedWarning = string.Empty;
        IsBusy = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = new AiMergeClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            var progress = new Progress<string>(text => ResultText = text);
            var result = await client.MergeNotesAsync(_transcriptionIds, Instruction, token, progress, CancellationToken.None);

            ResultText = result.Text;
            if (result.Truncated)
                TruncatedWarning = $"Algunas notas se recortaron para entrar en el límite (se incluyeron {result.IncludedCount} de {_transcriptionIds.Count}).";
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyMessage(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Copiar / Guardar como nota (mismo criterio de feedback que NoteDetailViewModel) --------

    private void ShowOperationStatus(string message)
    {
        OperationStatusText = message;
        _ = ClearOperationStatusAfterDelayAsync();
    }

    private async Task ClearOperationStatusAfterDelayAsync()
    {
        await Task.Delay(1800);
        OperationStatusText = string.Empty;
    }

    private bool CanCopyOrSaveResult() => !string.IsNullOrWhiteSpace(ResultText);

    /// <summary>Copia el documento unido (texto plano + HTML, ver <see cref="ClipboardService"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanCopyOrSaveResult))]
    private void CopyResult()
    {
        try
        {
            ClipboardService.CopyPlainAndHtml(ResultText, ClipboardHtmlBuilder.TextToHtmlFragment(ResultText));
            ShowOperationStatus("Copiado ✓");
        }
        catch (Exception ex)
        {
            ShowOperationStatus($"No se pudo copiar: {ex.Message}");
        }
    }

    /// <summary>Guarda el documento unido como nota nueva (ver <see cref="AiNotesClient"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanCopyOrSaveResult))]
    private async Task SaveAsNoteAsync()
    {
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = new AiNotesClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            await client.CreateNoteAsync(ResultText, token, CancellationToken.None);
            ShowOperationStatus("Guardado como nota ✓");
        }
        catch (Exception ex)
        {
            ShowOperationStatus($"No se pudo guardar: {ex.Message}");
        }
    }
}
