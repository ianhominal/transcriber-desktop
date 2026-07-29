namespace AudioTranscriber.Core.WebImport;

/// <summary>
/// Analiza una URL pegada por la usuaria SIN descargar nada: corre
/// <c>yt-dlp --dump-single-json --flat-playlist &lt;url&gt;</c> (vía <see cref="IYtDlpProcessRunner"/>,
/// nunca <see cref="System.Diagnostics.Process"/> directo -- así se puede testear con un doble de
/// prueba) y parsea el JSON con <see cref="YtDlpJsonParser"/>. Errores como ESTADOS
/// (<see cref="WebImportResult"/>), nunca excepciones -- salvo <see cref="OperationCanceledException"/>,
/// que se deja propagar tal cual (mismo criterio que <c>CloudTranscriptionService</c>: la
/// cancelación del usuario no es un "error" que la UI tenga que mostrar como tal).
/// <para/>
/// Terceros: esto descarga metadata (no el audio todavía) de sitios de terceros vía yt-dlp. El
/// cumplimiento de los Términos de Servicio de cada sitio corre por cuenta de quien use la app.
/// </summary>
public sealed class WebPageAnalyzer
{
    private readonly IYtDlpProcessRunner _runner;
    private readonly string _executablePath;

    public WebPageAnalyzer(IYtDlpProcessRunner runner, string executablePath)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Falta la ruta de yt-dlp.", nameof(executablePath));
        _executablePath = executablePath;
    }

    public async Task<WebImportResult> AnalyzeAsync(string? url, CancellationToken ct = default)
    {
        if (!IsValidHttpUrl(url, out var normalizedUrl))
            return WebImportResult.Failure(WebImportStatus.InvalidUrl, "La URL no es válida.");

        if (!File.Exists(_executablePath))
            return WebImportResult.Failure(
                WebImportStatus.YtDlpNotAvailable,
                "Todavía no se descargó yt-dlp, la herramienta necesaria para analizar páginas web.");

        YtDlpProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                _executablePath,
                new[] { "--dump-single-json", "--flat-playlist", "--no-warnings", normalizedUrl },
                onOutputLine: null,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WebImportResult.Failure(WebImportStatus.YtDlpFailed, $"No se pudo ejecutar yt-dlp: {ex.Message}");
        }

        if (result.ExitCode != 0)
        {
            var (status, message) = YtDlpErrorClassifier.Classify(result.ExitCode, result.StandardError);
            return WebImportResult.Failure(status, message);
        }

        try
        {
            var analysis = YtDlpJsonParser.Parse(result.StandardOutput);
            return WebImportResult.Success(analysis);
        }
        catch (YtDlpParseException ex)
        {
            return WebImportResult.Failure(ex.Status, ex.Message);
        }
    }

    /// <summary>
    /// Válida como URL http/https absoluta. No intenta adivinar si el SITIO está soportado por
    /// yt-dlp -- eso solo se sabe corriendo yt-dlp (ver <see cref="YtDlpErrorClassifier"/> para el
    /// caso "Unsupported URL" que devuelve el proceso).
    /// </summary>
    private static bool IsValidHttpUrl(string? url, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        normalized = trimmed;
        return true;
    }
}
