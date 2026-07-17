using AudioTranscriber.Core.Transcription;

namespace AudioTranscriber.Core.Diarization;

/// <summary>
/// Asegura que los DOS modelos que necesita la identificación de hablantes (sherpa-onnx) estén
/// disponibles en disco, descargándolos la primera vez -- mismo patrón que
/// <see cref="WhisperModelProvider"/> (descarga directa por HTTP con progreso real). A diferencia
/// del modelo de Whisper (~1,5 GB), acá son unas decenas de MB EN TOTAL: se bajan en segundos, no
/// minutos.
///
/// Son dos modelos con roles distintos, no intercambiables entre sí:
/// - Segmentación: dice CUÁNDO habla alguien (sin importar quién).
/// - Embeddings ("huella de voz"): compara qué tan parecidas son dos voces, para agrupar bajo el
///   mismo número de hablante los tramos que pertenecen a la misma persona.
///
/// URLs verificadas a mano (no son las de "GitHub Releases", que empaquetan el de segmentación
/// como .tar.bz2 y acá se necesita el .onnx suelto): son el mismo mirror sin comprimir que publica
/// el propio mantenedor de sherpa-onnx en Hugging Face. Devuelven HTTP 302 hacia un asset real con
/// el nombre de archivo esperado en el Content-Disposition -- ver .claude/resources/changelog para
/// el detalle de cómo se verificaron.
/// </summary>
public sealed class DiarizationModelProvider
{
    // Exportación a ONNX de pyannote/segmentation-3.0 hecha por csukuangfj (mantenedor de
    // sherpa-onnx). Mismo modelo que empaqueta el release "speaker-segmentation-models" de
    // GitHub, pero como archivo suelto (5,99 MB) en vez de .tar.bz2.
    private const string SegmentationUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx";

    // Embeddings de voz WeSpeaker (ResNet34, entrenado sobre VoxCeleb). No es un modelo entrenado
    // específicamente en español, pero VoxCeleb junta miles de entrevistas en muchos idiomas y
    // acentos: para distinguir VOCES (no palabras) generaliza razonablemente fuera del inglés. Es
    // el default que usan los propios ejemplos de sherpa-onnx para audio que no es en chino (que
    // tiene sus modelos "zh-cn" dedicados, más chicos si el audio fuera en ese idioma). Mismo
    // modelo que el release "speaker-recongition-models" de GitHub (26,5 MB), como archivo suelto.
    private const string EmbeddingUrl =
        "https://huggingface.co/csukuangfj/speaker-embedding-models/resolve/main/wespeaker_en_voxceleb_resnet34.onnx";

    private readonly string _modelDir;

    public DiarizationModelProvider(string modelDir)
    {
        _modelDir = modelDir ?? throw new ArgumentNullException(nameof(modelDir));
    }

    /// <summary>Ruta local del modelo de segmentación (quién habla cuándo).</summary>
    public string SegmentationModelPath => Path.Combine(_modelDir, "diarization-segmentation.onnx");

    /// <summary>Ruta local del modelo de embeddings (huella de voz).</summary>
    public string EmbeddingModelPath => Path.Combine(_modelDir, "diarization-embedding.onnx");

    public bool IsSegmentationModelAvailable => File.Exists(SegmentationModelPath);

    public bool IsEmbeddingModelAvailable => File.Exists(EmbeddingModelPath);

    /// <summary>True cuando los DOS modelos ya están en disco y se puede identificar hablantes.</summary>
    public bool IsModelAvailable => IsSegmentationModelAvailable && IsEmbeddingModelAvailable;

    /// <summary>
    /// Descarga el/los modelo/s que todavía falten (si ya están los dos, no hace nada).
    /// <paramref name="progress"/> reporta bytes recibidos/total del archivo que se está bajando
    /// en ese momento -- igual que <see cref="WhisperModelProvider.EnsureModelAsync"/>, la barra
    /// arranca de nuevo al pasar del primer archivo al segundo (son descargas independientes).
    /// </summary>
    public async Task EnsureModelAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (!IsSegmentationModelAvailable)
            await DownloadAsync(SegmentationUrl, SegmentationModelPath, progress, ct).ConfigureAwait(false);

        if (!IsEmbeddingModelAvailable)
            await DownloadAsync(EmbeddingUrl, EmbeddingModelPath, progress, ct).ConfigureAwait(false);
    }

    private async Task DownloadAsync(
        string url, string destPath, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_modelDir);
        var tempPath = destPath + ".partial";

        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0L;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var file = File.Create(tempPath))
            {
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

            File.Move(tempPath, destPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tempPath);
            throw;
        }
        catch
        {
            // A diferencia de WhisperModelProvider acá no hay una librería oficial de fallback
            // (WhisperGgmlDownloader es específico de Whisper.net): si la descarga directa falla,
            // el error se propaga tal cual para que la UI lo muestre.
            SafeDelete(tempPath);
            throw;
        }
    }

    private static void SafeDelete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
