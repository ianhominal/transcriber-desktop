using Whisper.net.Ggml;

namespace AudioTranscriber.Core.Transcription;

/// <summary>
/// Asegura que el modelo GGML de Whisper esté disponible en disco, descargándolo
/// la primera vez (una sola vez). Por defecto usa <see cref="GgmlType.Small"/>
/// cuantizado (Q5_0): buen balance calidad/velocidad para CPU modestas. Agnóstico de CUÁL modelo
/// es -- el catálogo de modelos seleccionables por la usuaria (ids/labels/fallback) vive en
/// <see cref="LocalModelOptions"/>; esta clase solo sabe bajar/ubicar el <see cref="GgmlType"/>
/// que le pasen.
///
/// Descarga directa desde Hugging Face para poder reportar progreso real
/// (Content-Length). Si esa descarga falla, cae al downloader oficial de Whisper.net.
/// </summary>
public sealed class WhisperModelProvider
{
    private const string BaseUrl = "https://huggingface.co/sandrohanea/whisper.net/resolve/main";

    private readonly string _modelDir;
    private readonly GgmlType _type;
    private readonly QuantizationType _quantization;

    public WhisperModelProvider(
        string modelDir,
        GgmlType type = GgmlType.Small,
        QuantizationType quantization = QuantizationType.Q5_0)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
        _type = type;
        _quantization = quantization;
    }

    /// <summary>Ruta local donde vive (o vivirá) el archivo del modelo.</summary>
    public string ModelPath =>
        Path.Combine(_modelDir, $"ggml-{_type}-{_quantization}.bin".ToLowerInvariant());

    public bool IsModelAvailable => File.Exists(ModelPath);

    /// <summary>Carpeta del repo HF según la cuantización (p. ej. "q5_0" o "classic").</summary>
    private string RemoteFolder =>
        _quantization == QuantizationType.NoQuantization
            ? "classic"
            : _quantization.ToString().ToLowerInvariant();

    /// <summary>
    /// URL directa del modelo en Hugging Face. Usa <see cref="LocalModelOptions.RemoteFileName"/>
    /// en vez de <c>_type.ToString().ToLowerInvariant()</c> a propósito: para los modelos Large ese
    /// ToString() da "largev3" (sin guión) mientras el archivo real es "ggml-large-v3.bin" (CON
    /// guión) -- 404 garantizado. Ver el XML doc de RemoteFileName para el detalle completo.
    /// </summary>
    private string DownloadUrl =>
        $"{BaseUrl}/{RemoteFolder}/ggml-{LocalModelOptions.RemoteFileName(_type)}.bin";

    /// <summary>
    /// Devuelve la ruta del modelo, descargándolo si aún no existe.
    /// <paramref name="progress"/> recibe bytes recibidos y total (para la barra).
    /// </summary>
    public async Task<string> EnsureModelAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (IsModelAvailable)
            return ModelPath;

        Directory.CreateDirectory(_modelDir);
        var tempPath = ModelPath + ".partial";

        try
        {
            await DownloadDirectAsync(tempPath, progress, ct).ConfigureAwait(false);
            File.Move(tempPath, ModelPath, overwrite: true);
            return ModelPath;
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tempPath);
            throw;
        }
        catch
        {
            // Fallback: si la descarga directa falla (p. ej. cambió el layout del repo),
            // usamos el downloader oficial de Whisper.net (sin progreso fino).
            SafeDelete(tempPath);
            await DownloadViaLibraryAsync(tempPath, ct).ConfigureAwait(false);
            File.Move(tempPath, ModelPath, overwrite: true);
            return ModelPath;
        }
    }

    private async Task DownloadDirectAsync(string tempPath, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http
            .GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0L;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(tempPath);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            progress?.Report(new ModelDownloadProgress(received, total));
        }
    }

    private async Task DownloadViaLibraryAsync(string tempPath, CancellationToken ct)
    {
        var downloader = WhisperGgmlDownloader.Default;
        await using var modelStream =
            await downloader.GetGgmlModelAsync(_type, _quantization, ct).ConfigureAwait(false);
        await using var file = File.Create(tempPath);
        await modelStream.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    private static void SafeDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
