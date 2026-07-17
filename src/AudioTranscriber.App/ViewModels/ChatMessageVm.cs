using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioTranscriber.App.ViewModels;

/// <summary>Estado de "Guardar como nota" para una respuesta puntual del chat (ver
/// <see cref="NoteDetailViewModel"/>) -- POR MENSAJE, no un booleano único, porque cualquier
/// respuesta del asistente puede guardarse, no solo la última (mismo criterio que
/// <c>saveNoteState</c> en <c>chat-panel.tsx</c> del backend web).</summary>
public enum ChatSaveNoteStatus { Idle, Saving, Saved, Error }

/// <summary>
/// Un mensaje del panel "Chat con IA" de <see cref="NoteDetailWindow"/>. A diferencia de los DTOs
/// inmutables de <see cref="AudioTranscriber.Core.Sync"/>, este es un objeto OBSERVABLE propio: en
/// la respuesta del asistente, <see cref="Text"/> se va reasignando en vivo a medida que llegan los
/// chunks <c>text-delta</c> del streaming (ver <see cref="AudioTranscriber.Core.Sync.AiChatClient.SendMessageAsync"/>),
/// y <see cref="SaveNoteStatus"/> cambia cuando la usuaria guarda esa respuesta como nota nueva.
/// </summary>
public sealed partial class ChatMessageVm : ObservableObject
{
    /// <summary>Id local, solo para que la UI tenga una key estable por mensaje -- no es
    /// necesariamente el mismo id que persiste el servidor en <c>chat_messages</c>.</summary>
    public string Id { get; }

    /// <summary>true = burbuja de la usuaria (derecha); false = burbuja del asistente (izquierda).</summary>
    public bool IsUser { get; }

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveNoteLabel))]
    [NotifyPropertyChangedFor(nameof(CanSaveAsNote))]
    private ChatSaveNoteStatus _saveNoteStatus = ChatSaveNoteStatus.Idle;

    [ObservableProperty]
    private string _saveNoteError = string.Empty;

    public ChatMessageVm(string id, bool isUser, string text)
    {
        Id = id;
        IsUser = isUser;
        _text = text;
    }

    /// <summary>Texto del botón "Guardar como nota" según <see cref="SaveNoteStatus"/> -- mismo
    /// patrón que <c>NoteDetailViewModel.SummarizeButtonText</c>.</summary>
    public string SaveNoteLabel => SaveNoteStatus switch
    {
        ChatSaveNoteStatus.Saving => "Guardando…",
        ChatSaveNoteStatus.Saved => "Guardado ✓",
        ChatSaveNoteStatus.Error => "Reintentar guardar",
        _ => "Guardar como nota",
    };

    /// <summary>false mientras se está guardando o ya se guardó -- no tiene sentido guardar la misma
    /// respuesta dos veces seguidas (mismo criterio que <c>chat-panel.tsx</c>).</summary>
    public bool CanSaveAsNote => SaveNoteStatus is ChatSaveNoteStatus.Idle or ChatSaveNoteStatus.Error;
}
