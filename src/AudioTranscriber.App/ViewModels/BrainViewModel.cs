using System.Collections.ObjectModel;
using System.Net.Http;
using AudioTranscriber.App; // MergeNotesWindow (reusado por CombineIntoDocumentCommand)
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

    /// <summary>Id remoto del proyecto cuando este chat está acotado a "Asistente del proyecto"
    /// (ver <see cref="ChatScopeRouter.Project"/>); <see langword="null"/> para "Todas mis notas"
    /// (constructor sin parámetros, botón global "Chat con IA").</summary>
    private readonly string? _projectId;

    private readonly string? _projectName;

    /// <summary>Notas del proyecto elegibles para "Combinar en documento" (ya filtradas por
    /// <see cref="AudioItemVm.RemoteId"/> no nulo y acotadas a <see cref="AiMergeClient.MaxNoteCount"/>
    /// por el caller, ver <c>MainViewModel.OpenProjectAssistant</c>). Null en el alcance global.</summary>
    private readonly IReadOnlyList<(string RemoteId, string Title)>? _mergeCandidates;

    public ObservableCollection<ChatMessageVm> Messages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    private string _question = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    [NotifyCanExecuteChangedFor(nameof(SummarizeCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextStepsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>Constructor de "Todas mis notas" (sin cambios de comportamiento respecto al
    /// original): <see cref="HasProjectScope"/> queda en false y la ventana muestra exactamente los
    /// mismos textos/controles que antes de "Asistente del proyecto".</summary>
    public BrainViewModel() { }

    /// <summary>Constructor de "Asistente del proyecto" (ver <c>MainViewModel.OpenProjectAssistantCommand</c>,
    /// abierto desde el nodo de un proyecto en el árbol).</summary>
    public BrainViewModel(string? projectId, string? projectName, IReadOnlyList<(string RemoteId, string Title)>? mergeCandidates)
    {
        _projectId = projectId;
        _projectName = projectName;
        _mergeCandidates = mergeCandidates;
    }

    /// <summary>True si este chat está acotado a un proyecto (ver constructor con <c>projectId</c>).</summary>
    public bool HasProjectScope => _projectId is not null;

    /// <summary>Título del encabezado de la ventana (bindeado en BrainWindow.xaml). Textos EXACTOS
    /// a los que ya mostraba la ventana antes de "Asistente del proyecto" para el alcance global,
    /// para que esa ventana quede pixel-idéntica a como estaba.</summary>
    public string HeaderTitle => HasProjectScope ? $"Asistente del proyecto: {_projectName}" : "Chat con IA";

    /// <summary>Subtítulo del encabezado de la ventana (bindeado en BrainWindow.xaml).</summary>
    public string ScopeDescription => HasProjectScope
        ? "Alcance: este proyecto. Preguntale a la IA: \"¿qué dije sobre X?\", \"juntá mis ideas sobre Y\"."
        : "Alcance: todas mis notas. Preguntale a la IA: \"¿qué dije sobre X?\", \"juntá mis ideas sobre Y\".";

    /// <summary>True si corresponde mostrar los atajos "Resumir"/"Próximos pasos"/"Combinar en
    /// documento" (solo en el alcance de proyecto, ver <see cref="ProjectQuickActions"/>).</summary>
    public bool ShowProjectQuickActions => HasProjectScope;

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
    /// Manda <see cref="Question"/> como pregunta nueva. Delega en <see cref="AskCoreAsync"/> (misma
    /// lógica compartida que usan los atajos "Resumir"/"Próximos pasos", ver
    /// <see cref="SummarizeAsync"/>/<see cref="NextStepsAsync"/>).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        var text = Question.Trim();
        if (text.Length == 0)
            return;

        await AskCoreAsync(text);
    }

    /// <summary>
    /// Lógica compartida de "hacer una pregunta" (extraída de <see cref="AskAsync"/> para que los
    /// atajos de proyecto -- <see cref="SummarizeAsync"/>/<see cref="NextStepsAsync"/> -- la
    /// reusen sin duplicar el manejo de burbujas/streaming/errores). Agrega la burbuja de la
    /// usuaria y una de asistente en placeholder ("Pensando…") de entrada --
    /// <see cref="AiBrainClient.AskAsync"/> reporta el texto ACUMULADO por <see cref="IProgress{T}"/>
    /// a medida que llega el streaming (mismo criterio que <c>NoteDetailViewModel.SendChatMessageAsync</c>).
    /// Pasa <c>_projectId</c> al cliente: en el alcance global queda <see langword="null"/>,
    /// idéntico al comportamiento de antes de "Asistente del proyecto".
    /// </summary>
    private async Task AskCoreAsync(string text)
    {
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
            var fullText = await client.AskAsync(text, token, progress, _askCts.Token, _projectId);
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

    // ---- Atajos de "Asistente del proyecto" (Resumir / Próximos pasos / Combinar en documento) ---

    private bool CanUseProjectQuickAction() => HasProjectScope && !IsBusy;

    /// <summary>Atajo "Resumir": manda <see cref="ProjectQuickActions.Summarize"/> como si la
    /// usuaria la hubiese tipeado (ver <see cref="AskCoreAsync"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanUseProjectQuickAction))]
    private async Task SummarizeAsync() => await AskCoreAsync(ProjectQuickActions.Summarize);

    /// <summary>Atajo "Próximos pasos": manda <see cref="ProjectQuickActions.NextSteps"/> como si la
    /// usuaria la hubiese tipeado (ver <see cref="AskCoreAsync"/>).</summary>
    [RelayCommand(CanExecute = nameof(CanUseProjectQuickAction))]
    private async Task NextStepsAsync() => await AskCoreAsync(ProjectQuickActions.NextSteps);

    private bool CanCombineIntoDocument() => HasProjectScope && AiMergeClient.CanMergeNoteCount(_mergeCandidates?.Count ?? 0);

    /// <summary>
    /// Abre <see cref="MergeNotesWindow"/> con las notas del proyecto (<see cref="_mergeCandidates"/>,
    /// ya resueltas por el caller -- ver <c>MainViewModel.OpenProjectAssistant</c>). Único punto de
    /// entrada a "Unir notas" desde el rediseño 2026-07-22 (el viejo modo checkbox multi-proyecto de
    /// <c>MainWindow</c> se eliminó): reusa la misma <see cref="MergeNotesWindow"/>/
    /// <see cref="MergeNotesViewModel"/>/<see cref="AiMergeClient"/> por debajo.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCombineIntoDocument))]
    private void CombineIntoDocument()
    {
        var window = new MergeNotesWindow(_mergeCandidates!) { Owner = System.Windows.Application.Current.MainWindow };
        window.Show();
    }
}
