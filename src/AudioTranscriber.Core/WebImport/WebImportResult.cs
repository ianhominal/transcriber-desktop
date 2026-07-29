namespace AudioTranscriber.Core.WebImport;

/// <summary>
/// Resultado discriminado de analizar o descargar desde una URL web. Errores como ESTADOS, no como
/// excepciones (a propósito -- ver <see cref="WebPageAnalyzer"/>/<see cref="WebAudioDownloader"/>):
/// la UI que consuma esto no necesita try/catch, solo mirar <c>Status</c> y mostrar
/// <c>ErrorMessage</c> tal cual (ya viene en español rioplatense, listo para la usuaria).
/// </summary>
public enum WebImportStatus
{
    Success,

    /// <summary>La URL no es una URL absoluta http/https válida.</summary>
    InvalidUrl,

    /// <summary>yt-dlp todavía no se descargó a esta máquina (ver <see cref="YtDlpProvider"/>).</summary>
    YtDlpNotAvailable,

    /// <summary>yt-dlp corrió pero terminó en error no clasificado en las otras categorías.</summary>
    YtDlpFailed,

    /// <summary>La página no tiene ningún audio/video que yt-dlp pueda extraer.</summary>
    NoAudioFound,

    /// <summary>El contenido es privado o exige haber iniciado sesión en el sitio de origen.</summary>
    RequiresLogin,

    /// <summary>No hay conexión a internet (o el sitio no responde).</summary>
    NoConnection,
}

/// <summary>Resultado de <see cref="WebPageAnalyzer.AnalyzeAsync"/>: qué se encontró en la URL, sin descargar nada.</summary>
public sealed record WebImportResult(WebImportStatus Status, WebMediaAnalysis? Analysis, string? ErrorMessage)
{
    public static WebImportResult Success(WebMediaAnalysis analysis) =>
        new(WebImportStatus.Success, analysis, null);

    public static WebImportResult Failure(WebImportStatus status, string message) =>
        new(status, null, message);
}

/// <summary>Resultado de <see cref="WebAudioDownloader.DownloadAsync"/>: la ruta del archivo bajado, o el estado de error.</summary>
public sealed record WebDownloadResult(WebImportStatus Status, string? FilePath, string? ErrorMessage)
{
    public static WebDownloadResult Success(string filePath) =>
        new(WebImportStatus.Success, filePath, null);

    public static WebDownloadResult Failure(WebImportStatus status, string message) =>
        new(status, null, message);
}
