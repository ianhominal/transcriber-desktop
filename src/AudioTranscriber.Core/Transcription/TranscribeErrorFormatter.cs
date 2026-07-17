using System;
using System.IO;
using System.Net.Http;

namespace AudioTranscriber.Core.Transcription;

/// <summary>
/// Turns a failure from <c>MainViewModel.TranscribeAsync</c> into something a person can act on.
/// Same mold as <c>WorkspaceErrorFormatter</c> (Core/Workspaces): the raw exception text must
/// NEVER reach the UI.
///
/// Real examples that reached StatusMessage before this fix (A.7, DESIGN-REVIEW-2026-07-16.md):
/// with Groq, "Error: The remote server returned an error: (401) Unauthorized"; with Local,
/// "Error: Could not find file 'C:\Users\ianho\...'" — English, no action, and with Local it
/// leaked the local path. TranscribeAsync's try block spans both engines plus the token refresh
/// (SyncCoordinator.GetValidAccessTokenAsync) and the transcript save, so several exception
/// shapes can reach this single catch.
/// </summary>
public static class TranscribeErrorFormatter
{
    /// <summary>Message for a failure inside TranscribeAsync (motor Local o Groq).</summary>
    public static string Friendly(Exception ex) => ex switch
    {
        // CloudTranscriptionException ya construye su propio mensaje amigable en español (ver
        // CloudTranscriptionService.BuildErrorMessage, incluye el caso 401/403 de sesión vencida):
        // se muestra tal cual, sin envolver de nuevo.
        CloudTranscriptionException => ex.Message,
        UnauthorizedAccessException => "No tenemos permiso para escribir el resultado. Elegí otra carpeta desde Configuración.",
        FileNotFoundException => "No se encontró el audio o el modelo. Puede que se hayan movido o borrado: probá de nuevo.",
        IOException => "No se pudo escribir la transcripción. Puede estar sincronizando o sin conexión: probá de nuevo en un momento.",
        HttpRequestException => "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.",
        _ => "No se pudo transcribir. Probá de nuevo.",
    };
}
