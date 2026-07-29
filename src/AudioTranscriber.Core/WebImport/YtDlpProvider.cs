namespace AudioTranscriber.Core.WebImport;

/// <summary>Progreso de descarga de <c>yt-dlp.exe</c>, mismo shape que <see cref="Transcription.ModelDownloadProgress"/>.</summary>
public readonly record struct YtDlpDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percent => TotalBytes > 0 ? BytesReceived * 100.0 / TotalBytes : 0;
}

/// <summary>
/// Asegura que <c>yt-dlp.exe</c> esté disponible en disco, descargándolo la primera vez. Mismo
/// criterio que <see cref="Transcription.WhisperModelProvider"/> (descarga directa con progreso
/// real vía Content-Length, archivo <c>.partial</c> + move atómico, sin reintentos automáticos): NO
/// se empaqueta en el instalador -- se baja on-demand la primera vez que la usuaria pega una URL,
/// mismo motivo que el modelo de Whisper (no inflar el instalador con algo que no todo el mundo usa).
/// <para/>
/// Se baja el binario standalone oficial (build de PyInstaller, no depende de tener Python
/// instalado) desde el release "latest" del repo de yt-dlp en GitHub.
/// </summary>
public sealed class YtDlpProvider
{
    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    private readonly string _installDir;

    public YtDlpProvider(string installDir)
    {
        _installDir = installDir ?? throw new ArgumentNullException(nameof(installDir));
    }

    public string ExecutablePath => Path.Combine(_installDir, "yt-dlp.exe");

    public bool IsAvailable => File.Exists(ExecutablePath);

    /// <summary>Devuelve la ruta del ejecutable, descargándolo si aún no existe.</summary>
    public async Task<string> EnsureAvailableAsync(IProgress<YtDlpDownloadProgress>? progress, CancellationToken ct)
    {
        if (IsAvailable)
            return ExecutablePath;

        Directory.CreateDirectory(_installDir);
        var tempPath = ExecutablePath + ".partial";

        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http
                .GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0L;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(tempPath))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new YtDlpDownloadProgress(received, total));
                }
            }

            File.Move(tempPath, ExecutablePath, overwrite: true);
            return ExecutablePath;
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            SafeDelete(tempPath);
            throw new YtDlpDownloadException(
                "Falló la descarga de yt-dlp. Revisá tu conexión e intentá de nuevo.", ex);
        }
    }

    private static void SafeDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

/// <summary>Falló la descarga del binario de yt-dlp. Mensaje ya listo para la UI.</summary>
public sealed class YtDlpDownloadException : Exception
{
    public YtDlpDownloadException(string message, Exception inner) : base(message, inner) { }
}
