using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using AudioTranscriber.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// ViewModel de la ventana NATIVA de gestión de "Vocabulario" (<see cref="VocabularyWindow"/>) --
/// diccionario custom de nombres/jerga que el usuario siempre corrige a mano (ver
/// <c>src/lib/vocabulary/*</c> del backend web, sección Ajustes → Vocabulario de la web). Mismo
/// patrón "Híbrido nativo" que <see cref="NoteDetailViewModel"/>: consume <c>/api/vocabulary*</c>
/// con el Bearer del sync, renderiza controles WPF reales. <see cref="GetAccessTokenOrThrowAsync"/>/
/// <see cref="FriendlyMessage"/> están REPLICADOS acá (no heredados de <see cref="NoteDetailViewModel"/>):
/// mismo criterio de "cada ViewModel es autónomo" que ya documentan <see cref="AiRecipesClient"/>/
/// <see cref="AiVocabularyClient"/> para sus propios helpers.
/// </summary>
public partial class VocabularyViewModel : ObservableObject
{
    public ObservableCollection<AiVocabularyTermDto> Terms { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTermCommand))]
    private string _newTermText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTermCommand))]
    private bool _isSaving;

    private static async Task<string> GetAccessTokenOrThrowAsync()
    {
        var token = await SyncCoordinator.Instance.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Iniciá sesión desde 'Sincronización' para gestionar tu vocabulario.");
        return token;
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        AiAssistException or InvalidOperationException => ex.Message,
        HttpRequestException => "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.",
        _ => $"Ocurrió un error inesperado: {ex.Message}",
    };

    private static AiVocabularyClient CreateClient() => new(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);

    /// <summary>Llamado desde <see cref="VocabularyWindow"/> en <c>Loaded</c>.</summary>
    public async Task InitializeAsync() => await LoadTermsCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task LoadTermsAsync()
    {
        ErrorMessage = string.Empty;
        IsLoading = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var terms = await CreateClient().ListTermsAsync(token, CancellationToken.None);

            Terms.Clear();
            foreach (var term in terms) Terms.Add(term);
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyMessage(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanAddTerm() => !string.IsNullOrWhiteSpace(NewTermText) && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanAddTerm))]
    private async Task AddTermAsync()
    {
        var term = NewTermText.Trim();
        ErrorMessage = string.Empty;
        IsSaving = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var created = await CreateClient().AddTermAsync(term, token, CancellationToken.None);

            Terms.Add(created);
            NewTermText = string.Empty;
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyMessage(ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// Edita un término existente: pide el nuevo texto con <see cref="InputDialog"/> (mismo diálogo
    /// simple de un campo que usa el resto de la app para renombrar) y lo manda al backend.
    /// </summary>
    [RelayCommand]
    private async Task EditTermAsync(AiVocabularyTermDto term)
    {
        var newText = InputDialog.Show("Editar término", "Término:", term.Term);
        if (newText is null || newText == term.Term)
            return;

        ErrorMessage = string.Empty;
        IsSaving = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var updated = await CreateClient().UpdateTermAsync(term.Id, newText, token, CancellationToken.None);

            var index = Terms.IndexOf(term);
            if (index >= 0) Terms[index] = updated;
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyMessage(ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task DeleteTermAsync(AiVocabularyTermDto term)
    {
        var confirm = MessageBox.Show(
            $"¿Borrar el término \"{term.Term}\" de tu vocabulario?",
            "Confirmar borrado", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        ErrorMessage = string.Empty;
        IsSaving = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            await CreateClient().DeleteTermAsync(term.Id, token, CancellationToken.None);

            Terms.Remove(term);
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyMessage(ex);
        }
        finally
        {
            IsSaving = false;
        }
    }
}
