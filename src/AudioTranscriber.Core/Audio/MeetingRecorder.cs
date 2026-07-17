using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Graba una REUNIÓN completa: mezcla el audio del sistema (lo que suena en la PC -- los demás
/// participantes de un Meet/Zoom) con el micrófono (vos), y lo escribe directo a un WAV 16 kHz
/// mono 16-bit -- el mismo formato que <see cref="MicrophoneRecorder"/>, listo para transcribir
/// sin conversión previa.
///
/// Dos trampas reales que resuelve (la lógica en sí vive en <see cref="AudioMixer"/>, ver el
/// comentario largo ahí -- acá solo se arma la plomería de captura alrededor):
///
/// 1. SILENCIOS: el audio del sistema no entrega nada mientras nadie habla. Concatenar solo lo
///    que llega comprimiría/desincronizaría la reunión.
/// 2. DERIVA DE RELOJES: micrófono y placa de sonido son dispositivos independientes; en una
///    reunión larga entregan cantidades de muestras distintas.
///
/// La solución: un <see cref="Timer"/> (el RELOJ DE PARED) dispara cada
/// <see cref="WriteIntervalMs"/> ms, calcula con <see cref="AudioMixer.SamplesOwed"/> cuántas
/// muestras deberían existir a esta altura, saca lo que haya en cada fuente (puede ser menos de lo
/// pedido, o nada) y llama a <see cref="AudioMixer.MixInto"/> con ese largo exacto -- lo que falte
/// queda en silencio. El archivo NUNCA se arma concatenando los buffers de captura directamente.
///
/// Cada fuente vive en su propio <see cref="BufferedWaveProvider"/> (con <c>ReadFully = false</c>,
/// así <c>Read</c> devuelve solo lo realmente disponible, nunca relleno con ceros) alimentado por
/// su hilo de captura; el hilo del Timer es el ÚNICO que lee de ahí. Es exactamente el patrón
/// productor-único/consumidor-único que ese buffer soporta con su propio lock interno -- no hace
/// falta ningún lock adicional para proteger las dos fuentes en sí, solo para coordinar Start/Stop
/// con el propio tick del Timer (ver <see cref="_writeLock"/>).
///
/// Si el audio del sistema no se puede capturar (sin dispositivo de salida, driver raro, lo que
/// sea), la grabación NO se cae: sigue solo con el micrófono y avisa por
/// <see cref="SystemAudioUnavailable"/> -- perder la reunión entera por un problema en UNA de las
/// dos fuentes sería peor que grabar la mitad.
/// </summary>
public sealed class MeetingRecorder : IDisposable
{
    private const int WriteIntervalMs = 100;

    /// <summary>Mismo formato que <see cref="MicrophoneRecorder"/>: el que exige Whisper.</summary>
    public static readonly WaveFormat RecordingFormat = MicrophoneRecorder.RecordingFormat;

    // Coordina Start/Stop con el propio tick del Timer -- ver el comentario de clase. NO protege
    // los BufferedWaveProvider de cada fuente: esos ya son productor-único/consumidor-único
    // (su propio CircularBuffer interno tiene lock).
    private readonly object _writeLock = new();
    private readonly Stopwatch _clock = new();

    private WaveInEvent? _waveIn;
    private BufferedWaveProvider? _micBuffered;
    private ISampleProvider? _micSamples;

    // IWaveIn (no WasapiLoopbackCapture concreto): puede ser un WasapiLoopbackCapture (todo el
    // sistema) O un ProcessLoopbackCapture (una sola app elegida) -- ver StartSystemAudio. Las dos
    // clases exponen la MISMA forma (WaveFormat/DataAvailable/RecordingStopped/StartRecording/
    // StopRecording/Dispose), así que todo lo de acá para abajo no necesita saber cuál es.
    private IWaveIn? _loopback;
    private BufferedWaveProvider? _loopbackBuffered;
    private ISampleProvider? _systemSamples;

    private WaveFileWriter? _writer;
    private Timer? _writeTimer;
    private long _samplesWritten;
    private bool _stopping;

    public bool IsRecording { get; private set; }
    public string? OutputPath { get; private set; }

    /// <summary>True si se pudo capturar el audio del sistema además del micrófono (de la app
    /// elegida, o de toda la PC si no se eligió ninguna, o si la app elegida falló y se cayó a
    /// todo el sistema -- ver <see cref="IsAppAudioCaptured"/> para distinguir esos dos últimos
    /// casos). Si es false, la grabación siguió solo con el micrófono -- ver
    /// <see cref="SystemAudioUnavailable"/>.</summary>
    public bool IsSystemAudioCaptured { get; private set; }

    /// <summary>
    /// True SOLO si se pidió capturar una aplicación puntual (<c>processId</c> en
    /// <see cref="Start"/>) y esa app puntual es lo que efectivamente se está grabando. False
    /// tanto si no se pidió ninguna app (se graba todo el sistema, caso normal) como si se pidió
    /// una y no se pudo -- en ese último caso se cayó a todo el sistema y avisa
    /// <see cref="AppAudioUnavailable"/> (ver la RED DE SEGURIDAD en el comentario de clase).
    /// </summary>
    public bool IsAppAudioCaptured { get; private set; }

    /// <summary>Nivel de audio normalizado (0.0-1.0) del micrófono, en cada buffer capturado (VU meter).</summary>
    public event Action<double>? LevelChanged;

    /// <summary>Se dispara si la captura del micrófono falla en segundo plano (p. ej. sin micrófono disponible).</summary>
    public event Action<Exception>? CaptureError;

    /// <summary>
    /// Se dispara si el audio del sistema (o de la app elegida, ver <see cref="AppAudioUnavailable"/>
    /// para ESE caso puntual) no se pudo capturar -- al arrancar o durante la grabación. La
    /// grabación NUNCA se corta por esto: sigue solo con el micrófono.
    /// </summary>
    public event Action<Exception>? SystemAudioUnavailable;

    /// <summary>
    /// Se dispara cuando se pidió capturar UNA aplicación puntual (<c>processId</c> en
    /// <see cref="Start"/>) y esa captura puntual falló -- la grabación NO se corta: cae a todo el
    /// audio de la PC (y, si ESO también falla, a <see cref="SystemAudioUnavailable"/> y de ahí a
    /// solo micrófono). Ver la RED DE SEGURIDAD en el comentario de clase.
    /// </summary>
    public event Action<Exception>? AppAudioUnavailable;

    /// <summary>
    /// Empieza a grabar, creando (si hace falta) la carpeta contenedora de <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="outputPath">Dónde queda el WAV final.</param>
    /// <param name="processId">
    /// Si se pasa, se intenta capturar SOLO el audio de esa aplicación (y sus procesos hijos --
    /// Chrome/Edge abren uno por pestaña) en vez de todo lo que suena en la PC. Si esa captura
    /// puntual falla por lo que sea, se cae a todo el sistema sin cortar la grabación -- ver
    /// <see cref="AppAudioUnavailable"/> y el comentario de clase (RED DE SEGURIDAD). <c>null</c>
    /// (default) mantiene el comportamiento de siempre: todo el audio de la PC.
    /// </param>
    public void Start(string outputPath, int? processId = null)
    {
        if (IsRecording)
            throw new InvalidOperationException("Ya hay una grabación en curso.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Falta la ruta de salida.", nameof(outputPath));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _writer = new WaveFileWriter(outputPath, RecordingFormat);
        OutputPath = outputPath;
        _samplesWritten = 0;
        _stopping = false;
        IsAppAudioCaptured = false;

        try
        {
            StartMicrophone();
        }
        catch
        {
            // Sin micrófono no hay grabación posible (a diferencia del audio del sistema, que es
            // best-effort -- ver StartSystemAudio): no dejamos un WAV a medio abrir.
            _writer.Dispose();
            _writer = null;
            OutputPath = null;
            throw;
        }

        StartSystemAudio(processId); // nunca tira, ver comentario del método.

        _clock.Restart();
        _writeTimer = new Timer(OnWriteTick, null, WriteIntervalMs, WriteIntervalMs);
        IsRecording = true;
    }

    private void StartMicrophone()
    {
        _micBuffered = new BufferedWaveProvider(RecordingFormat) { ReadFully = false, DiscardOnBufferOverflow = true };
        _micSamples = _micBuffered.ToSampleProvider();

        _waveIn = new WaveInEvent { WaveFormat = RecordingFormat, BufferMilliseconds = 100 };
        _waveIn.DataAvailable += OnMicDataAvailable;
        _waveIn.RecordingStopped += OnMicRecordingStopped;
        _waveIn.StartRecording();
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            _micBuffered?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            var pcm = e.Buffer.Length == e.BytesRecorded ? e.Buffer : e.Buffer[..e.BytesRecorded];
            LevelChanged?.Invoke(AudioLevelMeter.CalculateRms(pcm));
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(ex);
        }
    }

    private void OnMicRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            CaptureError?.Invoke(e.Exception);
    }

    /// <summary>Cuánto se espera a que Windows conteste el pedido de captura de UNA app antes de
    /// darla por perdida y caer a todo el sistema -- ver <see cref="StartSystemAudio"/>.</summary>
    private static readonly TimeSpan ProcessAudioActivationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Arranca la captura del audio del sistema -- de UNA app puntual si se pidió
    /// <paramref name="processId"/>, o de toda la PC si no. Nunca tira: cualquier falla queda
    /// contenida acá, con el orden de preferencia app elegida -&gt; todo el sistema -&gt; solo
    /// micrófono (ver el comentario de clase: perder la reunión entera por esto sería peor).
    /// </summary>
    private void StartSystemAudio(int? processId)
    {
        if (processId is int pid)
        {
            try
            {
                var appCapture = ProcessLoopbackCapture.ActivateForProcess(pid, ProcessAudioActivationTimeout);
                WireUpLoopbackSource(appCapture);
                appCapture.StartRecording();

                IsAppAudioCaptured = true;
                IsSystemAudioCaptured = true;
                return;
            }
            catch (Exception ex)
            {
                IsAppAudioCaptured = false;
                _systemSamples = null;
                _loopback?.Dispose();
                _loopback = null;
                _loopbackBuffered = null;
                AppAudioUnavailable?.Invoke(ex);
                // Sigue abajo: cae a todo el audio de la PC (ver comentario de clase).
            }
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var systemCapture = new WasapiLoopbackCapture(device);
            WireUpLoopbackSource(systemCapture);
            systemCapture.StartRecording();

            IsSystemAudioCaptured = true;
        }
        catch (Exception ex)
        {
            IsSystemAudioCaptured = false;
            _systemSamples = null;
            _loopback?.Dispose();
            _loopback = null;
            _loopbackBuffered = null;
            SystemAudioUnavailable?.Invoke(ex);
        }
    }

    /// <summary>
    /// Arma el pipeline común a las dos fuentes posibles del audio del sistema: buffer + downmix a
    /// mono + resample a 16 kHz (ver <see cref="DownmixToMonoSampleProvider"/> más abajo) y
    /// conecta los eventos de captura. <paramref name="capture"/> puede ser un
    /// <see cref="WasapiLoopbackCapture"/> (todo el sistema, formato nativo del dispositivo -- casi
    /// siempre float, 44.1/48 kHz, estéreo) o un <see cref="ProcessLoopbackCapture"/> (una sola
    /// app, formato PCM que nosotros mismos pedimos, ver esa clase): el pipeline de acá para abajo
    /// no necesita saber cuál de los dos es. NO arranca la captura -- eso queda para cada llamador,
    /// que además necesita ajustar sus propios flags (<see cref="IsAppAudioCaptured"/>) antes o
    /// después según corresponda.
    /// </summary>
    private void WireUpLoopbackSource(IWaveIn capture)
    {
        _loopback = capture;
        _loopbackBuffered = new BufferedWaveProvider(capture.WaveFormat)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
        };

        ISampleProvider native = _loopbackBuffered.ToSampleProvider();
        ISampleProvider mono = new DownmixToMonoSampleProvider(native);
        _systemSamples = mono.WaveFormat.SampleRate == RecordingFormat.SampleRate
            ? mono
            : new WdlResamplingSampleProvider(mono, RecordingFormat.SampleRate);

        capture.DataAvailable += OnSystemDataAvailable;
        capture.RecordingStopped += OnSystemRecordingStopped;
    }

    private void OnSystemDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            _loopbackBuffered?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
        catch (Exception ex)
        {
            // Best-effort: si el audio del sistema (o de la app elegida) se corta a mitad de
            // reunión, la grabación sigue solo con el micrófono (mismo criterio que un fallo al
            // arrancar, ver StartSystemAudio) -- acá no se reintenta caer de "app" a "todo el
            // sistema": una fuente que ya estaba andando y se corta es un caso distinto al de no
            // poder arrancarla, y reintentar a mitad de grabación sumaría más riesgo que beneficio.
            IsSystemAudioCaptured = false;
            IsAppAudioCaptured = false;
            SystemAudioUnavailable?.Invoke(ex);
        }
    }

    private void OnSystemRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
            return;
        IsSystemAudioCaptured = false;
        IsAppAudioCaptured = false;
        SystemAudioUnavailable?.Invoke(e.Exception);
    }

    private void OnWriteTick(object? state)
    {
        lock (_writeLock)
        {
            if (_stopping || _writer is null)
                return;
            WriteOwedSamples();
        }
    }

    /// <summary>
    /// El corazón del diseño (ver comentario de clase): calcula con
    /// <see cref="AudioMixer.SamplesOwed"/> cuántas muestras deberían existir según el RELOJ DE
    /// PARED, saca lo que haya de cada fuente (nunca bloquea: los <see cref="BufferedWaveProvider"/>
    /// con <c>ReadFully = false</c> devuelven lo que tengan, aunque sea menos de lo pedido o nada) y
    /// llama a <see cref="AudioMixer.MixInto"/> con ese largo exacto -- lo que falte queda en
    /// silencio.
    /// </summary>
    private void WriteOwedSamples()
    {
        var owed = AudioMixer.SamplesOwed(_clock.Elapsed, RecordingFormat.SampleRate, _samplesWritten);
        if (owed <= 0)
            return;

        var mic = new float[owed];
        var sys = new float[owed];
        int micLen = _micSamples?.Read(mic, 0, owed) ?? 0;
        int sysLen = _systemSamples?.Read(sys, 0, owed) ?? 0;

        var mixed = new float[owed];
        AudioMixer.MixInto(mixed, mic.AsSpan(0, micLen), sys.AsSpan(0, sysLen));

        _writer!.WriteSamples(mixed, 0, owed);
        _samplesWritten += owed;
    }

    /// <summary>Detiene la grabación y cierra el archivo WAV, dejándolo listo para transcribir.</summary>
    public void Stop()
    {
        if (!IsRecording)
            return;

        _writeTimer?.Dispose();
        _writeTimer = null;

        lock (_writeLock)
        {
            // Marca el corte ANTES de tocar nada: si un tick ya estaba en cola en el pool de
            // hilos cuando se disparó Dispose() de arriba, al entrar acá ve _stopping = true y no
            // toca un writer que ya estamos por cerrar.
            _stopping = true;
            WriteOwedSamples(); // último tramo, hasta el instante exacto de Stop.
        }

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnMicDataAvailable;
            _waveIn.RecordingStopped -= OnMicRecordingStopped;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }

        if (_loopback is not null)
        {
            _loopback.DataAvailable -= OnSystemDataAvailable;
            _loopback.RecordingStopped -= OnSystemRecordingStopped;
            _loopback.StopRecording();
            _loopback.Dispose(); // Dispose() espera (Join) a que el hilo de captura termine.
            _loopback = null;
        }

        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;

        _clock.Reset();
        IsRecording = false;
    }

    public void Dispose()
    {
        if (IsRecording)
            Stop();
    }

    /// <summary>
    /// Adaptador NAudio de <see cref="ChannelDownmixer.ToMono"/>: baja cualquier fuente
    /// multicanal a mono, sea cual sea la cantidad real de canales del dispositivo de salida
    /// (normalmente estéreo, pero no siempre -- placas 5.1/7.1).
    /// </summary>
    private sealed class DownmixToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _scratch = Array.Empty<float>();

        public DownmixToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int needed = count * _channels;
            if (_scratch.Length < needed)
                _scratch = new float[needed];

            int sourceRead = _source.Read(_scratch, 0, needed);
            return ChannelDownmixer.ToMono(_scratch.AsSpan(0, sourceRead), _channels, buffer.AsSpan(offset, count));
        }
    }
}
