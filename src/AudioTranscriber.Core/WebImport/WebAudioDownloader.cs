using System.Globalization;
using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.WebImport;

/// <summary>Progreso de descarga de un audio elegido, 0-100 (ver <see cref="YtDlpProgressLineParser"/>).</summary>
public readonly record struct WebDownloadProgress(double Percent);

/// <summary>
/// Descarga el audio de un <see cref="WebMediaItem"/> ya elegido por la usuaria (después de
/// <see cref="WebPageAnalyzer.AnalyzeAsync"/>) al workspace indicado. Pide <c>-f bestaudio</c> SIN
/// <c>--extract-audio</c> a propósito -- ver el comentario de <see cref="Audio.AudioConverter"/>:
/// esta app convierte audio en C# puro, no se mete ffmpeg acá; se baja el mejor stream de audio tal
/// cual como lo entregue el sitio y <see cref="Audio.AudioConverter"/> lo normaliza después.
/// <para/>
/// Terceros: esto descarga contenido real de sitios de terceros vía yt-dlp. El cumplimiento de los
/// Términos de Servicio de cada sitio corre por cuenta de quien use la app.
/// </summary>
public sealed class WebAudioDownloader
{
    private readonly IYtDlpProcessRunner _runner;
    private readonly string _executablePath;

    public WebAudioDownloader(IYtDlpProcessRunner runner, string executablePath)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Falta la ruta de yt-dlp.", nameof(executablePath));
        _executablePath = executablePath;
    }

    /// <summary>
    /// Descarga <paramref name="item"/> a <paramref name="destinationDirectory"/>.
    /// <paramref name="sourceUrl"/> es la URL original que la usuaria pegó (la página completa,
    /// playlist incluida) -- se usa como fallback cuando <see cref="WebMediaItem.Url"/> vino vacío
    /// (pasa con algunas entradas de playlist bajo <c>--flat-playlist</c>), pidiéndole a yt-dlp
    /// puntualmente ese ítem con <c>--playlist-items</c>.
    /// </summary>
    public async Task<WebDownloadResult> DownloadAsync(
        WebMediaItem item,
        string sourceUrl,
        string destinationDirectory,
        IProgress<WebDownloadProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Falta el destino de la descarga.", nameof(destinationDirectory));

        if (!File.Exists(_executablePath))
            return WebDownloadResult.Failure(
                WebImportStatus.YtDlpNotAvailable,
                "Todavía no se descargó yt-dlp, la herramienta necesaria para bajar audio.");

        Directory.CreateDirectory(destinationDirectory);
        var safeTitle = Workspace.Sanitize(item.Title);
        if (string.IsNullOrWhiteSpace(safeTitle))
            safeTitle = item.Id;
        var outputTemplate = Path.Combine(destinationDirectory, safeTitle + ".%(ext)s");

        var args = new List<string> { "-f", "bestaudio", "--no-warnings", "--newline", "-o", outputTemplate };
        var targetUrl = item.Url;
        if (targetUrl is null)
        {
            args.Add("--playlist-items");
            args.Add(item.Index.ToString(CultureInfo.InvariantCulture));
            targetUrl = sourceUrl;
        }
        args.Add(targetUrl);

        // SyncProgress, NO System.Progress&lt;T&gt;: Progress&lt;T&gt;.Report postea al SynchronizationContext
        // capturado en su constructor (para marshalear a la UI), lo que lo vuelve asincrónico incluso
        // sin contexto real (cae al thread pool) -- acá solo estamos reenviando el reporte al
        // `progress` que YA nos pasó el caller (que sí puede ser un Progress&lt;T&gt; real si quiere
        // marshalear a su UI), así que este paso intermedio tiene que ser síncrono.
        var onLine = progress is null
            ? null
            : new SyncProgress<string>(line =>
            {
                if (YtDlpProgressLineParser.TryParse(line, out var percent))
                    progress.Report(new WebDownloadProgress(percent));
            });

        YtDlpProcessResult result;
        try
        {
            result = await _runner.RunAsync(_executablePath, args, onLine, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WebDownloadResult.Failure(WebImportStatus.YtDlpFailed, $"No se pudo ejecutar yt-dlp: {ex.Message}");
        }

        if (result.ExitCode != 0)
        {
            var (status, message) = YtDlpErrorClassifier.Classify(result.ExitCode, result.StandardError);
            return WebDownloadResult.Failure(status, message);
        }

        var downloadedPath = FindDownloadedFile(destinationDirectory, safeTitle);
        if (downloadedPath is null)
            return WebDownloadResult.Failure(
                WebImportStatus.YtDlpFailed, "yt-dlp terminó pero no se encontró el archivo descargado.");

        return WebDownloadResult.Success(downloadedPath);
    }

    /// <summary>
    /// El template de salida usa <c>%(ext)s</c> porque la extensión real depende del stream que
    /// yt-dlp haya elegido (m4a/webm/opus/etc, según el sitio) y no se sabe de antemano -- por eso
    /// hay que buscar en disco el archivo que efectivamente quedó, en vez de asumir una extensión.
    /// </summary>
    private static string? FindDownloadedFile(string directory, string safeTitle) =>
        Directory.GetFiles(directory, safeTitle + ".*")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    /// <summary>
    /// <see cref="IProgress{T}"/> que invoca el callback EN EL MISMO HILO que llama a
    /// <see cref="Report"/>, sin marshalear a ningún <see cref="SynchronizationContext"/>. Ver el
    /// comentario donde se usa (arriba, en <see cref="DownloadAsync"/>).
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        public SyncProgress(Action<T> callback) => _callback = callback;
        public void Report(T value) => _callback(value);
    }
}
