using System;
using System.IO;

namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Turns a filesystem failure into something a person can act on.
///
/// The raw exception text must NEVER reach the UI. Real example we shipped: dropping a file on a
/// fresh install surfaced "No se pudieron agregar los archivos: The directory name
/// 'C:\Users\Sofia\OneDrive\Documentos\AudioTranscriber' does not exist. (Parameter 'path')" —
/// English, leaks the local path, and tells the user nothing they can do about it.
///
/// Cloud-synced folders (OneDrive/Drive) are the common source of these: Documents is often
/// redirected into OneDrive, which can move the folder, take it offline, or leave placeholders
/// that are not downloaded yet.
/// </summary>
public static class WorkspaceErrorFormatter
{
    /// <summary>Message for a failure while adding/copying audio files into the workspace.</summary>
    public static string FriendlyAddFilesError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "No tenemos permiso para escribir en la carpeta de trabajo. Elegí otra carpeta desde Configuración.",
        DirectoryNotFoundException => "No encontramos la carpeta de trabajo. Elegí una desde Configuración.",
        // FileSystemWatcher throws ArgumentException (not DirectoryNotFoundException) when its path
        // is missing — that exact case is what reached a real user.
        ArgumentException => "No encontramos la carpeta de trabajo. Elegí una desde Configuración.",
        IOException => "No se pudo escribir en la carpeta de trabajo. Puede estar sincronizando o sin conexión: probá de nuevo en un momento.",
        _ => "No se pudieron agregar los archivos. Probá de nuevo.",
    };

    /// <summary>Message for a failure while starting a recording into the workspace.</summary>
    public static string FriendlyRecordingError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "No tenemos permiso para grabar en la carpeta de trabajo. Elegí otra carpeta desde Configuración.",
        DirectoryNotFoundException or ArgumentException => "No encontramos la carpeta de trabajo. Elegí una desde Configuración.",
        IOException => "No se pudo escribir en la carpeta de trabajo. Puede estar sincronizando o sin conexión: probá de nuevo en un momento.",
        _ => "No se pudo empezar a grabar. Probá de nuevo.",
    };
}
