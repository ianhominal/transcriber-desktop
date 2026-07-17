namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Alcance del "Chat con IA" unificado del desktop (mismo criterio que la unificación hecha en la
/// web, commit <c>7f20a43</c>, "chat-panel.tsx" con selector "Esta nota" / "Todas mis notas"):
/// <see cref="ThisNote"/> usa <see cref="AiChatClient"/> (<c>POST /api/chat</c>, sobre UNA
/// transcripción puntual), <see cref="AllNotes"/> usa <see cref="AiBrainClient"/> (<c>POST
/// /api/brain</c>, retrieval global sobre TODAS las notas). Antes de este cambio eran dos features
/// separadas del lado del desktop (chat por nota en <c>NoteDetailWindow</c> vs. "Segundo cerebro" en
/// <c>BrainWindow</c>) -- ahora es un único selector.
///
/// Constantes de <see cref="string"/> (no <see langword="enum"/>) a propósito: mismo patrón que
/// <c>MainViewModel.Engine</c> ("local"/"groq"), pensado para bindear directo el <c>Tag</c> de un
/// <c>ComboBoxItem</c> en XAML sin necesitar markup extensions (<c>x:Static</c>) ni converters.
/// </summary>
public static class ChatScopeRouter
{
    /// <summary>"Esta nota": chat sobre UNA transcripción puntual (<see cref="AiChatClient"/>, necesita el <c>RemoteId</c> de la nota).</summary>
    public const string ThisNote = "note";

    /// <summary>"Todas mis notas": retrieval global sobre todas las notas del usuario (<see cref="AiBrainClient"/>, stateless, sin <c>RemoteId</c>).</summary>
    public const string AllNotes = "all";

    /// <summary>
    /// Mapeo PURO alcance -&gt; cliente (sin red, sin UI): true si <paramref name="scope"/>
    /// corresponde al chat global (<see cref="AiBrainClient"/>), false si corresponde al chat por
    /// nota (<see cref="AiChatClient"/>). Cualquier valor que no sea exactamente <see cref="AllNotes"/>
    /// cae en "Esta nota" -- mismo criterio permisivo que el resto de los switches de este proyecto,
    /// nunca tira por un valor inesperado. Lógica pura, extraída aparte para poder testearla sin
    /// mockear <see cref="System.Net.Http.HttpClient"/> ni instanciar un ViewModel de WPF.
    /// </summary>
    public static bool UsesGlobalBrainClient(string scope) => scope == AllNotes;
}
