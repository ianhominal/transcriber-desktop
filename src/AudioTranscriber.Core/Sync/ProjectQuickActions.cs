namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Textos preseteados de los atajos del "Asistente del proyecto" (ver <see cref="ChatScopeRouter.Project"/>):
/// botones "Resumir" / "Próximos pasos" que mandan una pregunta fija al Segundo cerebro acotada al
/// proyecto. MISMO texto en español que la web (<c>src/lib/chat/projectActions.ts</c>,
/// <c>PROJECT_QUICK_ACTION_MESSAGES</c>) a propósito: las dos apps le hacen la MISMA pregunta a la
/// IA, así que la respuesta es consistente sin importar desde dónde se pregunte. Constantes puras,
/// sin lógica -- guarda contra drift/typos accidental de este texto.
/// </summary>
public static class ProjectQuickActions
{
    public const string Summarize = "Resumí este proyecto en los puntos clave.";
    public const string NextSteps = "¿Cuáles son los próximos pasos o pendientes según estas notas?";
}
