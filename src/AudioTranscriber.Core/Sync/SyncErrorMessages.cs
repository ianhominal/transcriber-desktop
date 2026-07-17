namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Mapea una <see cref="SyncErrorCategory"/> a los textos que ve el usuario: la etiqueta corta del
/// chip de estado (<see cref="ChipFor"/>, usado por <c>SyncCoordinator.DisplayStatus</c>) y el
/// mensaje detallado (<see cref="StatusMessageFor"/>) que se muestra en el panel de SyncWindow.
/// Lógica pura, sin I/O.
/// </summary>
public static class SyncErrorMessages
{
    /// <summary>Etiqueta corta para el chip de estado (footer de MainWindow, menú de la bandeja).</summary>
    public static string ChipFor(SyncErrorCategory category) => category switch
    {
        SyncErrorCategory.NeedsLogin => "Iniciá sesión",
        SyncErrorCategory.NetworkError => "Sin conexión",
        SyncErrorCategory.ServerError => "Error del servidor, reintentando…",
        _ => "Error de sincronización",
    };

    /// <summary>
    /// Mensaje detallado para <c>SyncCoordinator.StatusMessage</c>. Para <see cref="SyncErrorCategory.Unknown"/>
    /// incluye <paramref name="exceptionMessage"/> -- el detalle completo (stack trace incluido) ya
    /// queda además en el log de sync (ver SyncErrorLogFormatter), esto es solo lo que ve el usuario.
    /// </summary>
    public static string StatusMessageFor(SyncErrorCategory category, string exceptionMessage) => category switch
    {
        SyncErrorCategory.NeedsLogin => "Iniciá sesión para sincronizar.",
        SyncErrorCategory.NetworkError => "Sin conexión. Reintentando cuando vuelva la red…",
        SyncErrorCategory.ServerError => "Error del servidor, reintentando…",
        _ => $"Error de sincronización: {exceptionMessage}",
    };
}
