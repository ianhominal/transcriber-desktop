using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using AudioTranscriber.Core.Export;
using AudioTranscriber.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// ViewModel del detalle NATIVO de una nota sincronizada (<see cref="NoteDetailWindow"/>) --
/// reemplaza al viejo WebView2 embebido (ver brief "Híbrido nativo" 2026-07-13): la app WPF
/// consume el MISMO backend que la web (<c>/api/summarize</c>, <c>/api/recipes*</c>,
/// <c>/api/chat</c>, <c>/api/brain</c>, <c>/api/notes</c>) con el Bearer del sync
/// (<see cref="SyncCoordinator.GetValidAccessTokenAsync"/>), pero renderiza controles WPF reales en
/// vez de una página completa. "Chat con IA" (2026-07-14, UNIFICADO ver <see cref="ChatScope"/>)
/// parsea el protocolo UIMessage/SSE del AI SDK a mano -- ver <see cref="AiChatClient"/> para el
/// wire format exacto -- a diferencia del streaming de texto plano de "Formatos"
/// (<see cref="ApplyRecipeAsync"/>).
/// </summary>
public partial class NoteDetailViewModel : ObservableObject
{
    /// <summary>Id remoto (Supabase) de la transcripción -- confirmado por el último sync exitoso, ver <see cref="AudioItemVm.RemoteId"/>.</summary>
    public string RemoteId { get; }

    /// <summary>Título de la nota (nombre del audio sin extensión), para el encabezado.</summary>
    public string Title { get; }

    /// <summary>Texto transcripto, tal como estaba cargado en el editor principal al abrir el detalle (solo lectura acá).</summary>
    public string TranscriptText { get; }

    public NoteDetailViewModel(string remoteId, string title, string transcriptText)
    {
        RemoteId = remoteId;
        Title = title;
        TranscriptText = transcriptText;
    }

    /// <summary>Llamado desde <see cref="NoteDetailWindow"/> en <c>Loaded</c>: carga los formatos del usuario.</summary>
    public async Task InitializeAsync() => await LoadRecipesCommand.ExecuteAsync(null);

    // ---- Helpers compartidos ---------------------------------------------------------------

    /// <summary>
    /// Devuelve un access token válido (refrescándolo si hace falta, mismo mecanismo que ya usa el
    /// ciclo de sync) o lanza <see cref="InvalidOperationException"/> con un mensaje ya pensado
    /// para mostrarse en la UI si no hay ninguna sesión guardada.
    /// </summary>
    private static async Task<string> GetAccessTokenOrThrowAsync()
    {
        var token = await SyncCoordinator.Instance.GetValidAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Iniciá sesión desde 'Sincronización' para usar las funciones de IA.");
        return token;
    }

    /// <summary>
    /// Traduce cualquier excepción de esta ventana a un mensaje en español, listo para pintarse tal
    /// cual. <see cref="AiAssistException"/> e <see cref="InvalidOperationException"/> ya traen un
    /// mensaje amigable (armado por <see cref="AiSummaryClient.BuildErrorMessage"/> /
    /// <see cref="AiRecipesClient.BuildErrorMessage"/> o por <see cref="GetAccessTokenOrThrowAsync"/>);
    /// cualquier otra excepción (bug, red no capturada) cae a un mensaje genérico.
    /// </summary>
    private static string FriendlyMessage(Exception ex) => ex switch
    {
        AiAssistException or InvalidOperationException => ex.Message,
        HttpRequestException => "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.",
        _ => $"Ocurrió un error inesperado: {ex.Message}",
    };

    // ---- Resumen ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SummarizeCommand))]
    private bool _isSummaryBusy;

    [ObservableProperty]
    private string _summaryError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummarizeButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CopySummaryCommand))]
    private bool _hasSummary;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySummaryCommand))]
    private string _summaryText = string.Empty;

    public ObservableCollection<string> KeyPoints { get; } = new();
    public ObservableCollection<string> ActionItems { get; } = new();

    /// <summary>"Resumir" la primera vez; "Regenerar" una vez que ya hay un resumen cargado (fuerza al backend a ignorar el cache).</summary>
    public string SummarizeButtonText => HasSummary ? "Regenerar" : "Resumir";

    private bool CanSummarize() => !IsSummaryBusy;

    [RelayCommand(CanExecute = nameof(CanSummarize))]
    private async Task SummarizeAsync()
    {
        SummaryError = string.Empty;
        IsSummaryBusy = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = new AiSummaryClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            var result = await client.SummarizeAsync(RemoteId, force: HasSummary, token, CancellationToken.None);

            SummaryText = result.Summary;
            KeyPoints.Clear();
            foreach (var point in result.KeyPoints) KeyPoints.Add(point);
            ActionItems.Clear();
            foreach (var item in result.ActionItems) ActionItems.Add(item);
            HasSummary = true;
        }
        catch (Exception ex)
        {
            SummaryError = FriendlyMessage(ex);
        }
        finally
        {
            IsSummaryBusy = false;
        }
    }

    // ---- Formatos -----------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isRecipesLoading;

    [ObservableProperty]
    private string _recipesError = string.Empty;

    public ObservableCollection<AiRecipeDto> Recipes { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecipeCommand))]
    private AiRecipeDto? _selectedRecipe;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecipeCommand))]
    private bool _isApplyingRecipe;

    [ObservableProperty]
    private string _recipeError = string.Empty;

    /// <summary>Salida del formato aplicado, actualizada EN VIVO a medida que llega el streaming (ver <see cref="ApplyRecipeAsync"/>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyRecipeOutputCommand))]
    private string _recipeOutputText = string.Empty;

    [RelayCommand]
    private async Task LoadRecipesAsync()
    {
        RecipesError = string.Empty;
        IsRecipesLoading = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = new AiRecipesClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            var recipes = await client.ListRecipesAsync(token, CancellationToken.None);

            Recipes.Clear();
            foreach (var recipe in recipes) Recipes.Add(recipe);
            SelectedRecipe ??= Recipes.FirstOrDefault(r => r.IsDefault) ?? Recipes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            RecipesError = FriendlyMessage(ex);
        }
        finally
        {
            IsRecipesLoading = false;
        }
    }

    private bool CanApplyRecipe() => SelectedRecipe is not null && !IsApplyingRecipe;

    /// <summary>
    /// Aplica <see cref="SelectedRecipe"/> a la nota. El backend responde en streaming de texto
    /// plano (<c>toTextStreamResponse</c> del AI SDK -- sin protocolo, los bytes SON el texto), así
    /// que <see cref="AiRecipesClient.ApplyRecipeAsync"/> reporta el acumulado por
    /// <see cref="IProgress{T}"/> y acá solo hace falta pintarlo tal cual llega (texto apareciendo
    /// en vivo, ver <see cref="RecipeOutputText"/>). Distinto del chat (<c>/api/chat</c>, ver
    /// <see cref="SendChatMessageAsync"/>), que usa el protocolo UIMessage/SSE del AI SDK.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyRecipe))]
    private async Task ApplyRecipeAsync()
    {
        if (SelectedRecipe is not { } recipe)
            return;

        RecipeError = string.Empty;
        RecipeOutputText = string.Empty;
        IsApplyingRecipe = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            var client = new AiRecipesClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
            // Progress<T> despacha al SynchronizationContext capturado al construirse -- acá, el
            // Dispatcher de la UI (este método corre desde un RelayCommand disparado por un botón),
            // así que asignar RecipeOutputText directo en el callback es seguro.
            var progress = new Progress<string>(text => RecipeOutputText = text);
            await client.ApplyRecipeAsync(RemoteId, recipe.Id, token, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            RecipeError = FriendlyMessage(ex);
        }
        finally
        {
            IsApplyingRecipe = false;
        }
    }

    // ---- Chat con IA -------------------------------------------------------------------------

    /// <summary>Token de cancelación del envío en curso -- permite "Detener" (ver <see cref="StopChatAsync"/>)
    /// sin abortar la ventana entera. Uno solo a la vez: el input queda deshabilitado mientras
    /// <see cref="IsChatBusy"/> es true, así que nunca hay dos envíos simultáneos que pisarse.</summary>
    private CancellationTokenSource? _chatCts;

    public ObservableCollection<ChatMessageVm> ChatMessages { get; } = new();

    /// <summary>Alcance del "Chat con IA" unificado (2026-07-14, ver <see cref="ChatScopeRouter"/>):
    /// "Esta nota" (default, <see cref="ChatScopeRouter.ThisNote"/>) usa <see cref="AiChatClient"/>
    /// sobre <see cref="RemoteId"/>; "Todas mis notas" (<see cref="ChatScopeRouter.AllNotes"/>) usa
    /// <see cref="AiBrainClient"/>, mismo criterio que <see cref="BrainViewModel"/>. Reemplaza las
    /// dos features separadas que tenía el desktop (chat por nota vs. "Segundo cerebro"), igual que
    /// la unificación ya hecha en la web (commit 7f20a43).</summary>
    [ObservableProperty]
    private string _chatScope = ChatScopeRouter.ThisNote;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChatMessageCommand))]
    private string _chatInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChatMessageCommand))]
    private bool _isChatBusy;

    [ObservableProperty]
    private string _chatError = string.Empty;

    private bool CanSendChatMessage() => !IsChatBusy && !string.IsNullOrWhiteSpace(ChatInput);

    /// <summary>
    /// Manda <see cref="ChatInput"/> como mensaje nuevo del chat. Agrega la burbuja de la usuaria y
    /// una burbuja de asistente en placeholder ("Pensando…") de entrada -- el cliente usado depende
    /// de <see cref="ChatScope"/> (ver <see cref="ChatScopeRouter.UsesGlobalBrainClient"/>):
    /// <see cref="AiChatClient.SendMessageAsync"/> para "Esta nota", <see cref="AiBrainClient.AskAsync"/>
    /// para "Todas mis notas". Ambos reportan el texto ACUMULADO de la respuesta vía
    /// <see cref="IProgress{T}"/> a medida que llega el streaming (ver <see cref="ApplyRecipeAsync"/>
    /// para el mismo patrón con "Formatos"), y el primer chunk ya pisa el placeholder con contenido
    /// real -- no hace falta un estado "pensando" aparte en el ViewModel.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendChatMessage))]
    private async Task SendChatMessageAsync()
    {
        var text = ChatInput.Trim();
        if (text.Length == 0)
            return;

        ChatError = string.Empty;
        ChatInput = string.Empty;

        ChatMessages.Add(new ChatMessageVm(Guid.NewGuid().ToString(), isUser: true, text));
        var assistantMessage = new ChatMessageVm(Guid.NewGuid().ToString(), isUser: false, "Pensando…");
        ChatMessages.Add(assistantMessage);

        _chatCts = new CancellationTokenSource();
        IsChatBusy = true;
        try
        {
            var token = await GetAccessTokenOrThrowAsync();
            // Progress<T> despacha al Dispatcher de la UI capturado acá -- mismo criterio que
            // ApplyRecipeAsync, seguro porque este método corre desde un RelayCommand disparado
            // por la UI.
            var progress = new Progress<string>(chunk => assistantMessage.Text = chunk);

            string fullText;
            if (ChatScopeRouter.UsesGlobalBrainClient(ChatScope))
            {
                var client = new AiBrainClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
                fullText = await client.AskAsync(text, token, progress, _chatCts.Token);
            }
            else
            {
                var client = new AiChatClient(SyncCoordinator.Instance.Http, SyncConfig.BackendBaseUrl);
                fullText = await client.SendMessageAsync(RemoteId, text, token, progress, _chatCts.Token);
            }
            assistantMessage.Text = fullText;
        }
        catch (OperationCanceledException)
        {
            // "Detener": no es un error -- se queda con el texto parcial ya recibido en la burbuja.
        }
        catch (Exception ex)
        {
            ChatMessages.Remove(assistantMessage);
            ChatError = FriendlyMessage(ex);
        }
        finally
        {
            IsChatBusy = false;
            _chatCts?.Dispose();
            _chatCts = null;
        }
    }

    /// <summary>Cancela el envío en curso (botón "Detener", visible mientras <see cref="IsChatBusy"/>).</summary>
    [RelayCommand]
    private void StopChat() => _chatCts?.Cancel();

    // ---- Copiar con formato / Exportar (2026-07-14) ------------------------------------------
    // Feedback compartido para ambas features: se muestra cerca del botón "Copiar"/"Exportar" en
    // el panel de transcripción Y en el encabezado "Asistente IA" (misma instancia de VM, dos
    // lugares del XAML) -- ver NoteDetailWindow.xaml. Se limpia solo, 1.8s después de cada acción.

    [ObservableProperty]
    private string _operationStatusText = string.Empty;

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

    private void CopyWithFeedback(string plainText, string htmlFragment)
    {
        try
        {
            ClipboardService.CopyPlainAndHtml(plainText, htmlFragment);
            ShowOperationStatus("Copiado ✓");
        }
        catch (Exception ex)
        {
            ShowOperationStatus($"No se pudo copiar: {ex.Message}");
        }
    }

    /// <summary>Copia la transcripción (texto plano + HTML, ver <see cref="ClipboardService"/>).</summary>
    [RelayCommand]
    private void CopyTranscript() =>
        CopyWithFeedback(TranscriptText, ClipboardHtmlBuilder.TextToHtmlFragment(TranscriptText));

    private bool CanCopySummary() => HasSummary && !string.IsNullOrWhiteSpace(SummaryText);

    /// <summary>Copia el resumen generado con IA.</summary>
    [RelayCommand(CanExecute = nameof(CanCopySummary))]
    private void CopySummary() =>
        CopyWithFeedback(SummaryText, ClipboardHtmlBuilder.TextToHtmlFragment(SummaryText));

    private bool CanCopyRecipeOutput() => !string.IsNullOrWhiteSpace(RecipeOutputText);

    /// <summary>Copia la salida del formato aplicado.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyRecipeOutput))]
    private void CopyRecipeOutput() =>
        CopyWithFeedback(RecipeOutputText, ClipboardHtmlBuilder.TextToHtmlFragment(RecipeOutputText));

    /// <summary>
    /// Exporta la nota (título + fecha + transcripción + resumen si ya se generó) a .md/.txt/.docx/
    /// .pdf, elegido por la extensión que la usuaria pone en el <see cref="SaveFileDialog"/> (mismo
    /// criterio "sin abstracción de diálogos" que ya usa <c>MainViewModel</c> -- ver
    /// <c>ChooseExportFolder</c> -- para <see cref="Microsoft.Win32.OpenFolderDialog"/>).
    /// </summary>
    [RelayCommand]
    private void ExportNote()
    {
        var dialog = new SaveFileDialog
        {
            FileName = NoteContentExporter.BuildFileName(Title, DateTime.Now, ".md"),
            Filter = "Markdown (*.md)|*.md|Texto (*.txt)|*.txt|Word (*.docx)|*.docx|PDF (*.pdf)|*.pdf",
            DefaultExt = ".md",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var content = BuildExportContent();
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            switch (extension)
            {
                case ".txt":
                    File.WriteAllText(dialog.FileName, NoteContentExporter.BuildPlainText(content));
                    break;
                case ".docx":
                    DocxExporter.Build(dialog.FileName, content);
                    break;
                case ".pdf":
                    PdfExporter.Build(dialog.FileName, content);
                    break;
                default: // ".md" y cualquier otra extensión: markdown (mismo default que el Filter).
                    File.WriteAllText(dialog.FileName, NoteContentExporter.BuildMarkdown(content));
                    break;
            }
            ShowOperationStatus($"Exportado a {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            ShowOperationStatus($"No se pudo exportar: {ex.Message}");
        }
    }

    private NoteExportContent BuildExportContent() => new(
        Title,
        DateTime.Now,
        TranscriptText,
        HasSummary ? SummaryText : null,
        HasSummary ? KeyPoints.ToList() : Array.Empty<string>(),
        HasSummary ? ActionItems.ToList() : Array.Empty<string>());

    /// <summary>
    /// Guarda el texto de <paramref name="message"/> (una respuesta del asistente) como una nota
    /// nueva vía <c>POST /api/notes</c> (ver <see cref="AiNotesClient"/>). Guardado independiente
    /// por mensaje (<see cref="ChatMessageVm.SaveNoteStatus"/>), no un booleano único del ViewModel
    /// -- cualquier respuesta puede guardarse, no solo la última.
    /// </summary>
    [RelayCommand]
    private async Task SaveChatMessageAsNoteAsync(ChatMessageVm? message)
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
