using NAudio.Wave;

namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Graba desde el micrófono directo a un WAV PCM 16 kHz mono 16-bit: el mismo formato que exige
/// Whisper (<see cref="AudioConverter.TargetSampleRate"/>), así el archivo grabado se puede
/// transcribir después sin ninguna conversión previa. Expone el nivel de audio en vivo (RMS,
/// vía <see cref="AudioLevelMeter"/>) para pintar un VU meter simple en la UI.
/// </summary>
public sealed class MicrophoneRecorder : IDisposable
{
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    public static readonly WaveFormat RecordingFormat = new(AudioConverter.TargetSampleRate, BitsPerSample, Channels);

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;

    public bool IsRecording { get; private set; }
    public string? OutputPath { get; private set; }

    /// <summary>Nivel de audio normalizado (0.0–1.0), reportado en cada buffer capturado.</summary>
    public event Action<double>? LevelChanged;

    /// <summary>Se dispara si la captura falla en segundo plano (p. ej. sin micrófono disponible).</summary>
    public event Action<Exception>? CaptureError;

    /// <summary>Empieza a grabar, creando (si hace falta) la carpeta contenedora de <paramref name="outputPath"/>.</summary>
    public void Start(string outputPath)
    {
        if (IsRecording)
            throw new InvalidOperationException("Ya hay una grabación en curso.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Falta la ruta de salida.", nameof(outputPath));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _waveIn = new WaveInEvent { WaveFormat = RecordingFormat, BufferMilliseconds = 100 };
        _writer = new WaveFileWriter(outputPath, RecordingFormat);
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        OutputPath = outputPath;
        IsRecording = true;
        _waveIn.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            var pcm = e.Buffer.Length == e.BytesRecorded ? e.Buffer : e.Buffer[..e.BytesRecorded];
            LevelChanged?.Invoke(AudioLevelMeter.CalculateRms(pcm));
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            CaptureError?.Invoke(e.Exception);
    }

    /// <summary>Detiene la grabación y cierra el archivo WAV, dejándolo listo para transcribir.</summary>
    public void Stop()
    {
        if (!IsRecording || _waveIn is null)
            return;

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveIn = null;

        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;

        IsRecording = false;
    }

    public void Dispose()
    {
        if (IsRecording)
            Stop();
    }
}
