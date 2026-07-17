using System.Collections.ObjectModel;
using System.Net.Http;
using AudioTranscriber.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// ViewModel de "Segundo cerebro" (<see cref="BrainWindow"/>): preguntarle a la IA sobre TODAS las
/// notas del usuario, no una transcripción puntual (ver <see cref="AiBrainClient"/>, brief "Híbrido
/// nativo" 2026-07-14). Reusa <see cref="ChatMessageVm"/> (mismas burbujas que el chat de
/// <c>NoteDetailWindow</c>) -- el backend es STATELESS (cada pregunta es independiente, ver header
/// comment de <see cref="AiBrainClient"/>), pero la UI igual muestra la conversación completa
/// localmente (mismo criterio que <c>brain-chat.tsx</c> del backend web). Helpers REPLICADOS (no
/// heredados), mismo criterio que <see cref="FormatosViewModel"/>/<see cref="NoteDetailViewModel"/>.
/// </summary>
public partial class BrainViewModel : ObservableObject
{
    private CancellationTokenSource? _askCts;

    public ObservableCollection<ChatMessageVm> Messages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private string _question = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    private static async Task<string> GetAccessTokenOrThrowAsync()
    {
        var token = await SyncCoordinator.Instance.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Iniciá sesión desde 'Sincronización' para usar el Chat con IA.");
        return token;
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        AiAssistException or InvalidOperationException => ex.Message,
        HttpRequestException => "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.",
        _ => $"Ocurrió un error inesperado: {ex.Message}",
    };

    private bool CanAsk() => !IsBusy && !string.IsNullOrWhiteSpace(Question);

    /// <summary>
    /// Manda <see cref="Question"/> como pregunta nueva. Agrega la burbuja de la usuaria y una de
    /// asistente en placeholder ("Pensando…") de entrada -- <see cref="AiBrainClient.AskAsync"/>
    /// reporta el texto ACUMULADO por <see cref="IProgress{T}"/> a medida que llega el streaming
    /// (mismo criterio que <c>NoteDetailViewModel.SendChatMessageAsync</c>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        var text = Question.Trim();
        if (text.Length == 0)
            return;

        ErrorMessage = string.Empty;
        Question = string.Empty;

        Messages.Add(new ChatMessageVm(Guid.NewGuid().ToString(), isUser: true, text));
        var assistantMessage = new ChatMessageVm(Guid.NewGuid().ToString(), isUser: false, "Pensando…");
        Messages.Add(assistantMessage);

        _askCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = new AiBrainClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            var progress = new Progress<string>(chunk => assistantMessage.Text = chunk);
            var fullText = await client.AskAsync(text, token, progress, _askCts.Token);
            assistantMessage.Text = fullText;
        }
        catch (OperationCanceledException)
        {
            // "Detener": no es un error -- se queda con el texto parcial ya recibido en la burbuja.
        }
        catch (Exception ex)
        {
            Messages.Remove(assistantMessage);
            ErrorMessage = FriendlyMessage(ex);
        }
        finally
        {
            IsBusy = false;
            _askCts?.Dispose();
            _askCts = null;
        }
    }

    /// <summary>Cancela la pregunta en curso (botón "Detener", visible mientras <see cref="IsBusy"/>).</summary>
    [RelayCommand]
    private void Stop() => _askCts?.Cancel();

    /// <summary>Guarda la respuesta como nota nueva (ver <see cref="AiNotesClient"/>), mismo criterio
    /// por-mensaje que <c>NoteDetailViewModel.SaveChatMessageAsNoteAsync</c>.</summary>
    [RelayCommand]
    private async Task SaveMessageAsNoteAsync(ChatMessageVm? message)
    {
        if (message is null || message.IsUser || !message.CanSaveAsNote)
            return;

        var text = message.Text.Trim();
        if (text.Length == 0)
            return;

        message.SaveNoteError = string.Empty;
        message.SaveNoteStatus = ChatSaveNoteStatus.Saving;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = new AiNotesClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            await client.CreateNoteAsync(text, token, CancellationToken.None);
            message.SaveNoteStatus = ChatSaveNoteStatus.Saved;
        }
        catch (Exception ex)
        {
            message.SaveNoteStatus = ChatSaveNoteStatus.Error;
            message.SaveNoteError = FriendlyMessage(ex);
        }
    }
}
