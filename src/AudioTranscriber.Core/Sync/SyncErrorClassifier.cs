namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Categorías en las que se clasifica un error de sync fallido, para poder mostrarle al usuario un
/// mensaje útil (ver <see cref="SyncErrorMessages"/>) en vez de un "Error" genérico. Lógica pura:
/// solo mira el tipo/StatusCode/mensaje de la excepción, sin I/O ni dependencias de WPF.
/// </summary>
public enum SyncErrorCategory
{
    /// <summary>
    /// La sesión guardada quedó inválida: refresh rechazado por Supabase (400), o el backend
    /// rechazó el pull/push/subida con 401/403 (incluido "refresh_token_not_found" en el body). El
    /// usuario tiene que volver a iniciar sesión -- no es un bug de la app.
    /// </summary>
    NeedsLogin,

    /// <summary>
    /// Fallo de transporte: sin conexión, DNS, timeout de HttpClient. No llegó a haber respuesta
    /// HTTP del backend.
    /// </summary>
    NetworkError,

    /// <summary>El backend respondió pero con un error del lado del servidor (5xx).</summary>
    ServerError,

    /// <summary>Cualquier otro error no clasificado (bug del cliente, deserialización, etc.).</summary>
    Unknown,
}

/// <summary>Clasifica la excepción de un ciclo de sync fallido en una <see cref="SyncErrorCategory"/>.</summary>
public static class SyncErrorClassifier
{
    public static SyncErrorCategory Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (IsInvalidSession(ex))
            return SyncErrorCategory.NeedsLogin;

        if (IsServerError(ex))
            return SyncErrorCategory.ServerError;

        if (IsNetworkError(ex))
            return SyncErrorCategory.NetworkError;

        return SyncErrorCategory.Unknown;
    }

    // Mismo criterio que ya usaba SyncCoordinator.RunSyncAsync inline: 400 del refresh de Supabase,
    // o 401/403 del backend al rechazar pull/push/subida, o "refresh_token_not_found" en el body
    // (puede venir con otro status según el endpoint).
    private static bool IsInvalidSession(Exception ex) =>
        ex is SyncAuthException { StatusCode: 400 }
        || ex is SyncApiException { StatusCode: 401 or 403 }
        || (ex is SyncApiException && ex.Message.Contains("refresh_token_not_found", StringComparison.OrdinalIgnoreCase));

    private static bool IsServerError(Exception ex) =>
        ex is SyncApiException { StatusCode: >= 500 and <= 599 }
        || ex is SyncAuthException { StatusCode: >= 500 and <= 599 };

    // HttpRequestException: sin conexión, DNS, TLS, conexión rechazada -- no llegó a haber respuesta
    // HTTP (por eso SyncApiException/SyncAuthException no aplican acá: esas solo se tiran cuando SÍ
    // hubo respuesta pero no fue exitosa). TaskCanceledException/OperationCanceledException: timeout
    // de HttpClient (.NET tira TaskCanceledException, a veces con TimeoutException como inner).
    private static bool IsNetworkError(Exception ex) =>
        ex is HttpRequestException
        || ex is TaskCanceledException
        || ex is OperationCanceledException
        || ex is System.Net.Sockets.SocketException;
}
