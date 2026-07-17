using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using AudioTranscriber.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// ViewModel de la ventana NATIVA de gestión de "Formatos" (<see cref="FormatosWindow"/>):
/// crear, editar, borrar y marcar default (ver <c>src/lib/recipes/*</c> del backend web, sección
/// Ajustes → Formatos de la web). El detalle de una nota (<see cref="NoteDetailViewModel"/>) solo
/// LISTA y APLICA formatos a una transcripción puntual -- esta ventana es la gestión completa del
/// catálogo del usuario. Mismo criterio "Híbrido nativo" y helpers REPLICADOS (no heredados) que
/// <see cref="VocabularyViewModel"/>.
/// </summary>
public partial class FormatosViewModel : ObservableObject
{
    public ObservableCollection<AiRecipeDto> Recipes { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isSaving;

    private static async Task<string> GetAccessTokenOrThrowAsync()
    {
        var token = await SyncCoordinator.Instance.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Iniciá sesión desde 'Sincronización' para gestionar tus formatos.");
        return token;
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        AiAssistException or InvalidOperationException => ex.Message,
        HttpRequestException => "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.",
        _ => $"Ocurrió un error inesperado: {ex.Message}",
    };

    private static AiRecipesClient CreateClient() => new(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);

    /// <summary>Llamado desde <see cref="FormatosWindow"/> en <c>Loaded</c>.</summary>
    public async Task InitializeAsync() => await LoadRecipesCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task LoadRecipesAsync()
    {
        ErrorMessage = string.Empty;
        IsLoading = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var recipes = await CreateClient().ListRecipesAsync(token, CancellationToken.None);

            Recipes.Clear();
            foreach (var recipe in recipes) Recipes.Add(recipe);
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

    /// <summary>
    /// Crea un formato nuevo: pide nombre + instrucción con <see cref="RecipeEditDialog"/> y
    /// recarga la lista completa al terminar (más simple y a prueba de errores que mantener el
    /// <c>ObservableCollection</c> en sync a mano -- el catálogo está topeado a 30 formatos, el
    /// costo de un round-trip extra es insignificante).
    /// </summary>
    [RelayCommand]
    private async Task CreateRecipeAsync()
    {
        var input = RecipeEditDialog.Show("Nuevo formato", "Creá un formato reutilizable para aplicar a tus notas.");
        if (input is not { } value)
            return;

        ErrorMessage = string.Empty;
        IsSaving = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            await CreateClient().CreateRecipeAsync(value.Name, value.Instruction, token, CancellationToken.None);
            await LoadRecipesCommand.ExecuteAsync(null);
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
    private async Task EditRecipeAsync(AiRecipeDto recipe)
    {
        var input = RecipeEditDialog.Show("Editar formato", "Actualizá el nombre o la instrucción.", recipe.Name, recipe.Instruction);
        if (input is not { } value)
            return;

        ErrorMessage = string.Empty;
        IsSaving = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            await CreateClient().UpdateRecipeAsync(recipe.Id, value.Name, value.Instruction, token, CancellationToken.None);
            await LoadRecipesCommand.ExecuteAsync(null);
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
    private async Task SetDefaultRecipeAsync(AiRecipeDto recipe)
    {
        if (recipe.IsDefault)
            return;

        ErrorMessage = string.Empty;
        IsSaving = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            await CreateClient().SetDefaultRecipeAsync(recipe.Id, token, CancellationToken.None);
            await LoadRecipesCommand.ExecuteAsync(null);
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
    private async Task DeleteRecipeAsync(AiRecipeDto recipe)
    {
        var confirm = MessageBox.Show(
            $"¿Borrar el formato \"{recipe.Name}\"?",
            "Confirmar borrado", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        ErrorMessage = string.Empty;
        IsSaving = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            await CreateClient().DeleteRecipeAsync(recipe.Id, token, CancellationToken.None);
            Recipes.Remove(recipe);
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
