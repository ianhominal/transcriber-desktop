using NAudio.Wave;
using SherpaOnnx;

namespace AudioTranscriber.Core.Diarization;

/// <summary>
/// Corre el modelo de identificación de hablantes (sherpa-onnx, 100% local/offline) sobre un WAV
/// ya convertido y devuelve QUIÉN habla y CUÁNDO -- no transcribe nada, eso ya lo hizo Whisper por
/// separado (ver <see cref="AudioTranscriber.Core.Transcription.TranscriptionService"/>). Cruzar
/// ambos resultados es trabajo de <see cref="SpeakerAssigner"/>, no de esta clase.
///
/// Corre sobre CPU y puede tardar: se ejecuta en un thread de fondo (<see cref="Task.Run"/>) para
/// no bloquear la UI, igual que Whisper.
/// </summary>
public sealed class DiarizationService
{
    private readonly DiarizationModelProvider _modelProvider;

    public DiarizationService(DiarizationModelProvider modelProvider)
    {
        _modelProvider = modelProvider ?? throw new ArgumentNullException(nameof(modelProvider));
    }

    /// <summary>
    /// Identifica los tramos de cada hablante en <paramref name="wavPath"/> (debe ser WAV 16 kHz
    /// mono -- el mismo formato que ya usa Whisper, ver
    /// <see cref="AudioTranscriber.Core.Audio.AudioConverter.ToWhisperWav"/>). No sabemos de
    /// antemano cuánta gente participa de la reunión, así que la cantidad de hablantes se DETECTA
    /// sola por similitud de voz -- nunca se fija un número.
    /// </summary>
    public async Task<IReadOnlyList<SpeakerSegment>> DiarizeAsync(
        string wavPath, IProgress<string>? onLog, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
            throw new FileNotFoundException("No se encontró el audio a analizar.", wavPath);

        // Chequeo explícito ANTES de tocar la librería nativa: del lado de .NET, sherpa-onnx no
        // valida las rutas de los modelos por su cuenta (son punteros que se pasan directo a
        // código nativo) -- un modelo faltante acá podría fallar de una forma poco clara en vez de
        // un mensaje entendible.
        if (!_modelProvider.IsModelAvailable)
            throw new InvalidOperationException(
                "Faltan los modelos para identificar quién habla (todavía no se descargaron).");

        ct.ThrowIfCancellationRequested();

        return await Task.Run(() => Run(wavPath, onLog, ct), ct).ConfigureAwait(false);
    }

    private IReadOnlyList<SpeakerSegment> Run(string wavPath, IProgress<string>? onLog, CancellationToken ct)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = _modelProvider.SegmentationModelPath;
        config.Embedding.Model = _modelProvider.EmbeddingModelPath;

        // Cantidad de hablantes DESCONOCIDA de antemano (es una reunión real, no un audio de
        // prueba con N voces fijas): NumClusters se deja en -1 ("sin fijar"; es además el default
        // de la propia librería) y se usa Threshold, el mecanismo que sherpa-onnx ofrece para este
        // caso exacto -- ver el ejemplo oficial dotnet-examples/offline-speaker-diarization.
        config.Clustering.NumClusters = -1;

        // 0.40, MEDIDO sobre audios reales de usuarios (2026-07-16), no elegido de memoria. Antes
        // estaba en 0.50 y fusionaba voces obviamente distintas:
        //
        //   Entrevista (periodista + Harry Kane, 129s):
        //     0.50 -> 1 voz  (126s a una sola: la periodista desaparecía)
        //     0.45 -> 1 voz
        //     0.40 -> 2 voces (105s + 21s)  <- la proporción real de una entrevista
        //
        //   Transmisión de TV (relator + tanda publicitaria, 154s):
        //     0.50 -> el relator se comía la tanda
        //     0.40 -> relator 80s + tanda 14s, separados
        //
        // Más abajo de 0.40 empieza a partir UNA voz en varias (a 0.20 daba 7 "personas" para un
        // solo relator), así que no se baja más. El umbral es qué tan distintas tienen que sonar dos
        // voces para contarlas como personas separadas: alto = fusiona gente, bajo = duplica gente.
        //
        // Calibrado con 3 audios reales: es evidencia, no una constante universal. Si aparecen casos
        // donde parte a una misma persona en dos, hay que volver a medir antes de tocarlo.
        config.Clustering.Threshold = 0.4f;

        using var diarizer = new OfflineSpeakerDiarization(config);

        var (samples, sampleRate) = ReadSamples(wavPath);
        if (diarizer.SampleRate != sampleRate)
            throw new InvalidOperationException(
                $"El audio convertido no tiene la frecuencia que espera el modelo ({diarizer.SampleRate} Hz).");

        ct.ThrowIfCancellationRequested();

        var callback = new OfflineSpeakerDiarizationProgressCallback((processed, total, _) =>
        {
            if (total > 0)
                onLog?.Report($"Identificando quién habla… {processed * 100 / total}%");
            return 0;
        });

        var result = diarizer.ProcessWithCallback(samples, callback, IntPtr.Zero);

        // sherpa-onnx no documenta con certeza si el callback puede abortar el proceso nativo
        // devolviendo un valor distinto de 0 -- por las dudas no confiamos en eso: chequeamos acá,
        // así una cancelación real del usuario SIEMPRE se nota (nunca se ignora en silencio),
        // aunque en el peor caso haya que esperar a que termine la pasada actual.
        ct.ThrowIfCancellationRequested();

        var segments = new List<SpeakerSegment>(result.Length);
        foreach (var s in result)
            segments.Add(MapSegment(s.Start, s.End, s.Speaker));
        return segments;
    }

    /// <summary>
    /// Traduce un resultado crudo de sherpa-onnx (segundos en <see cref="float"/>, id de hablante)
    /// a nuestro <see cref="SpeakerSegment"/> (<see cref="TimeSpan"/>). Separado de <see cref="Run"/>
    /// -- que necesita un modelo real cargado -- para poder testear esta conversión sola, con
    /// números de prueba, sin tocar la librería nativa ni ningún archivo.
    /// </summary>
    public static SpeakerSegment MapSegment(float startSeconds, float endSeconds, int speaker) =>
        new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), speaker);

    /// <summary>
    /// Lee un WAV como muestras float normalizadas a [-1, 1], que es lo que pide sherpa-onnx.
    ///
    /// El paquete NuGet <c>org.k2fsa.sherpa.onnx</c> NO trae un lector de WAV: el <c>WaveReader</c>
    /// que aparece en los ejemplos oficiales de GitHub (dotnet-examples/offline-speaker-diarization)
    /// vive en un proyecto de ejemplo aparte (dotnet-examples/Common), no en la librería en sí --
    /// verificado por reflection contra el ensamblado instalado, ver .claude/resources/changelog.
    /// En vez de copiar ese archivo de ejemplo, reusamos NAudio (ya es dependencia del proyecto,
    /// ver <see cref="AudioTranscriber.Core.Audio.AudioConverter"/>): <c>ToSampleProvider()</c>
    /// hace exactamente esa conversión PCM→float sin que tengamos que escribirla a mano.
    /// </summary>
    private static (float[] Samples, int SampleRate) ReadSamples(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        var sampleRate = reader.WaveFormat.SampleRate;
        var sampleProvider = reader.ToSampleProvider();

        var samples = new List<float>();
        var buffer = new float[81920];
        int read;
        while ((read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
                samples.Add(buffer[i]);
        }

        return (samples.ToArray(), sampleRate);
    }
}
