using System.Diagnostics;
using System.Text;
using AudioTranscriber.Core.Audio;
using NAudio.Wave;
using Whisper.net;

namespace AudioTranscriber.Core.Transcription;

/// <summary>
/// Orquesta el pipeline completo: asegura el modelo, convierte el audio al formato
/// que Whisper exige (WAV 16 kHz mono) y transcribe emitiendo segmentos a medida
/// que se detectan. La <see cref="WhisperFactory"/> se carga una sola vez y se reutiliza.
/// </summary>
public sealed class TranscriptionService : IAsyncDisposable
{
    private readonly WhisperModelProvider _modelProvider;
    private readonly AudioConverter _converter;
    private WhisperFactory? _factory;

    /// <summary>Idioma del audio. "es" = español; "auto" = detección automática.</summary>
    public string Language { get; set; } = "es";

    /// <summary>
    /// Ruta del último WAV convertido (16 kHz mono) si se pidió conservarlo con
    /// <c>keepConvertedWav: true</c> -- pensado para que la identificación de hablantes
    /// (<see cref="AudioTranscriber.Core.Diarization.DiarizationService"/>) reuse ESE MISMO
    /// archivo en vez de convertir el audio una segunda vez. Null si no se pidió conservarlo o si
    /// ya se limpió con <see cref="DeleteLastConvertedWav"/>.
    /// </summary>
    public string? LastConvertedWavPath { get; private set; }

    public TranscriptionService(WhisperModelProvider modelProvider, AudioConverter? converter = null)
    {
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
        _converter = converter ?? new AudioConverter();
    }

    /// <summary>
    /// Transcribe <paramref name="audioPath"/> (mp3/wav) y devuelve el texto completo.
    /// <paramref name="onSegment"/> recibe cada segmento en streaming (para ir mostrando
    /// el avance en la UI). Cancelable vía <paramref name="ct"/>.
    /// </summary>
    /// <param name="keepConvertedWav">
    /// Si es true, el WAV 16 kHz mono que se convierte acá NO se borra al terminar -- queda
    /// disponible en <see cref="LastConvertedWavPath"/> para que otro paso (identificación de
    /// hablantes) lo reuse sin convertir el audio de nuevo. El caller es responsable de borrarlo
    /// después con <see cref="DeleteLastConvertedWav"/>. Default false: mismo comportamiento de
    /// siempre (se borra acá).
    /// </param>
    public async Task<string> TranscribeAsync(
        string audioPath,
        IProgress<TranscriptSegment>? onSegment,
        CancellationToken ct,
        IProgress<ModelDownloadProgress>? onDownload = null,
        IProgress<double>? onProgress = null,
        IProgress<string>? onLog = null,
        bool keepConvertedWav = false)
    {
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("No se encontró el audio a transcribir.", audioPath);

        var totalSw = Stopwatch.StartNew();
        var sw = new Stopwatch();

        // 1) Asegurar el modelo (descarga si falta)
        if (!_modelProvider.IsModelAvailable)
            onLog?.Report("Modelo no encontrado: descargando (una única vez)…");
        var modelPath = await _modelProvider.EnsureModelAsync(onDownload, ct).ConfigureAwait(false);

        // 2) Cargar el modelo en memoria (la primera vez de la sesión es lo más pesado)
        if (_factory is null)
        {
            onLog?.Report("Cargando modelo en memoria…");
            sw.Restart();
            _factory = WhisperFactory.FromPath(modelPath);
            onLog?.Report($"Modelo cargado en {sw.Elapsed.TotalSeconds:0.0}s.");
        }
        else
        {
            onLog?.Report("Modelo ya cargado en memoria (reutilizado).");
        }

        await using var processor = _factory.CreateBuilder()
            .WithLanguage(Language)
            .Build();

        var tempWav = Path.Combine(Path.GetTempPath(), "at_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            // 3) Convertir el audio al formato de Whisper (WAV 16 kHz mono)
            onLog?.Report($"Convirtiendo audio ({Path.GetExtension(audioPath).TrimStart('.').ToUpperInvariant()})…");
            sw.Restart();
            _converter.ToWhisperWav(audioPath, tempWav, ct);

            TimeSpan totalDuration;
            using (var probe = new WaveFileReader(tempWav))
                totalDuration = probe.TotalTime;
            onLog?.Report($"Audio convertido en {sw.Elapsed.TotalSeconds:0.0}s (duración {totalDuration.TotalSeconds:0}s).");

            // 4) Transcribir en streaming
            onLog?.Report("Transcribiendo… (en CPU esto puede tardar)");
            sw.Restart();
            var sb = new StringBuilder();
            await using var wavStream = File.OpenRead(tempWav);
            await foreach (var segment in processor.ProcessAsync(wavStream, ct).ConfigureAwait(false))
            {
                sb.Append(segment.Text);
                onSegment?.Report(new TranscriptSegment(segment.Start, segment.End, segment.Text));
                onLog?.Report($"[{Fmt(segment.Start)}→{Fmt(segment.End)}] {segment.Text.Trim()}");

                if (totalDuration > TimeSpan.Zero)
                {
                    var percent = Math.Min(100.0, segment.End / totalDuration * 100.0);
                    onProgress?.Report(percent);
                }
            }

            onProgress?.Report(100.0);
            var speed = totalDuration.TotalSeconds > 0
                ? sw.Elapsed.TotalSeconds / totalDuration.TotalSeconds
                : 0;
            onLog?.Report(
                $"Listo en {sw.Elapsed.TotalSeconds:0.0}s de transcripción " +
                $"({speed:0.0}x el largo del audio). Total: {totalSw.Elapsed.TotalSeconds:0.0}s.");

            return sb.ToString().Trim();
        }
        finally
        {
            // keepConvertedWav: no lo borramos acá -- queda para que el caller (después de
            // intentar identificar hablantes, con éxito o no) lo borre una sola vez llamando a
            // DeleteLastConvertedWav(). Si el archivo nunca llegó a crearse (falló antes de la
            // conversión), File.Exists ya lo filtra en ambos casos.
            if (keepConvertedWav && File.Exists(tempWav))
                LastConvertedWavPath = tempWav;
            else if (File.Exists(tempWav))
                File.Delete(tempWav);
        }
    }

    /// <summary>
    /// Borra el WAV conservado por <c>keepConvertedWav</c> (ver <see cref="TranscribeAsync"/>).
    /// Llamar siempre al terminar de usarlo, se haya podido identificar hablantes o no -- no tira
    /// si no hay nada que borrar.
    /// </summary>
    public void DeleteLastConvertedWav()
    {
        if (LastConvertedWavPath is { } path && File.Exists(path))
            File.Delete(path);
        LastConvertedWavPath = null;
    }

    private static string Fmt(TimeSpan t) => t.ToString(@"mm\:ss");

    public ValueTask DisposeAsync()
    {
        _factory?.Dispose();
        _factory = null;
        return ValueTask.CompletedTask;
    }
}
