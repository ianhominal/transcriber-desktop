namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Construye la URL de la pantalla de Ajustes en la web, donde se configura Google Drive (el
/// desktop no maneja Drive directamente, ver comentario en <see cref="SyncConfig"/>). Lógica pura
/// separada de SettingsWindow para poder testear la construcción de la URL sin abrir un navegador
/// real ni depender de <see cref="System.Diagnostics.Process"/>.
/// </summary>
public static class GoogleDriveLinkBuilder
{
    private const string AjustesPath = "/app/ajustes";

    /// <summary>
    /// URL de Ajustes en la web a partir de la base del backend. Recorta cualquier "/" final de
    /// <paramref name="baseUrl"/> para no terminar con "//app/ajustes" si ya trae uno.
    /// </summary>
    public static string BuildSettingsUrl(string baseUrl) => baseUrl.TrimEnd('/') + AjustesPath;
}
