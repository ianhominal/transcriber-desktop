using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;

namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Reproduce cualquier audio soportado (mp3/wav/ogg/opus/mp4/m4a/webm) con volumen, posición y
/// seek. Para OGG/Opus intenta primero el códec nativo de Windows (rápido); si no está, cae al
/// decodificador Concentus (lento, pero solo como respaldo).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    // Protege TODO acceso a _reader/_output en Play/Stop/Seek/OnPlaybackStopped: estos métodos se
    // llaman desde hasta 3 hilos distintos (un Task.Run del threadpool que dispara Play/Stop desde
    // MainViewModel, el hilo de UI directo, y el hilo interno de NAudio que dispara
    // PlaybackStopped cuando el audio termina solo). Sin esto, un check-then-use no atómico sobre
    // _reader en OnPlaybackStopped (p.ej. reproducir un clip corto y tocar "siguiente" justo cuando
    // termina) podía tirar NullReferenceException/ObjectDisposedException en el hilo de NAudio
    // (no-Dispatcher), lo que tumbaba el proceso entero vía AppDomain.UnhandledException.
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private float _volume = 0.5f;

    /// <summary>Se dispara cuando la reproducción termina sola (no por Stop()).</summary>
    public event Action? PlaybackEnded;

    /// <summary>Volumen 0.0–1.0. Se aplica en vivo.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            // Bajo _gate: sin esto, el check-then-use de _output (leerlo para el null-check y otra
            // vez para asignarle Volume) puede cruzarse con un Stop() concurrente que lo dispone en
            // el medio -> ObjectDisposedException. Misma clase de bug que _gate resuelve para el
            // resto de los accesos a _output/_reader.
            lock (_gate)
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_output is not null)
                    _output.Volume = _volume;
            }
        }
    }

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan CurrentTime => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan TotalTime => _reader?.TotalTime ?? TimeSpan.Zero;

    /// <summary>
    /// Prepara y arranca la reproducción. La decodificación (que para OGG puede ser
    /// costosa) ocurre acá, por eso conviene llamarlo desde un hilo en segundo plano.
    /// </summary>
    public void Play(string path)
    {
        Stop();

        // El reader/output nuevos se arman en variables locales (CreateReader puede ser lento para
        // OGG/Opus, ver doc de la clase) y recién se publican a los campos compartidos bajo el
        // lock, en una sección corta que no llama a NAudio -- así ningún hilo concurrente ve un
        // estado a medio construir. output.Play() queda AFUERA del lock a propósito: WaveOutEvent
        // arranca su propio hilo de reproducción ahí, y no hace falta (ni conviene) tener el lock
        // tomado mientras eso ocurre.
        var reader = CreateReader(path);
        var output = new WaveOutEvent();
        output.Init(reader);
        output.Volume = _volume;
        output.PlaybackStopped += OnPlaybackStopped;

        lock (_gate)
        {
            _reader = reader;
            _output = output;
        }

        output.Play();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Terminó de sonar (llegó al final) o NAudio paró solo por un error (e.Exception != null,
        // ver fix #4: antes esto se ignoraba y la UI quedaba pegada en "Reproduciendo" para
        // siempre). Todo el acceso a _reader pasa por el lock -- este handler corre en el hilo
        // interno de NAudio y puede solaparse con un Play()/Stop() disparado desde otro hilo.
        bool ended;
        lock (_gate)
        {
            ended = e.Exception is not null
                || (_reader is not null && _reader.CurrentTime >= _reader.TotalTime - TimeSpan.FromMilliseconds(300));
        }

        // El evento se despacha a un hilo del threadpool (NO sincrónicamente) a propósito. Este
        // handler corre en el hilo INTERNO de reproducción de NAudio; un subscriber de PlaybackEnded
        // que directa o indirectamente llame a Stop() haría que Stop() -> _output.Stop() ejecute
        // Join() sobre ESTE mismo hilo -> ambos se esperarían -> deadlock que congela la app en cada
        // fin natural de reproducción. En producción pasa exactamente eso: MainViewModel suscribe
        // PlaybackEnded -> Dispatcher.Invoke(StopAudio) -> _player.Stop(). Al disparar el evento vía
        // Task.Run, este handler retorna de inmediato y el hilo de NAudio puede terminar, así el
        // Join() posterior no bloquea, sin importar qué haga el subscriber.
        if (ended)
        {
            var handler = PlaybackEnded;
            if (handler is not null)
                Task.Run(() => handler.Invoke());
        }
    }

    public void Pause() => _output?.Pause();
    public void Resume() => _output?.Play();

    public void Stop()
    {
        // Se capturan y desenganchan las referencias bajo el lock (rápido, sin llamadas a NAudio) y
        // recién se para/libera FUERA del lock. Motivo: WaveOutEvent.Stop()/Dispose() esperan
        // (Join) a que termine su hilo interno de reproducción, y ese mismo hilo es quien dispara
        // OnPlaybackStopped -- si Stop() llamara a _output.Stop() CON el lock tomado, y
        // OnPlaybackStopped necesita ese mismo lock para leer _reader, quedarían esperándose
        // mutuamente: este método bloqueado en Join() y el hilo de NAudio bloqueado en el lock ->
        // deadlock. Por eso el teardown real de NAudio corre siempre sin el lock tomado.
        WaveOutEvent? output;
        WaveStream? reader;

        lock (_gate)
        {
            output = _output;
            reader = _reader;
            _output = null;
            _reader = null;
        }

        if (output is not null)
            output.PlaybackStopped -= OnPlaybackStopped;
        output?.Stop();
        output?.Dispose();
        reader?.Dispose();
    }

    /// <summary>Salta a un instante del audio.</summary>
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_reader is null)
                return;
            var clamped = position < TimeSpan.Zero ? TimeSpan.Zero
                        : position > _reader.TotalTime ? _reader.TotalTime
                        : position;
            _reader.CurrentTime = clamped;
        }
    }

    private static WaveStream CreateReader(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("No se encontró el audio.", path);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".mp4":
            case ".m4a":
            case ".aac":
            case ".webm":
                // WebM/Opus (recordings downloaded from the web, see Workspace.SupportedExtensions
                // doc): Windows Media Foundation decodes it directly on this environment (verified
                // against real downloaded files -- opens, reports correct WaveFormat/TotalTime, and
                // reads PCM). No fallback here: Concentus below only understands Ogg framing, not
                // WebM/Matroska, so it cannot demux this container.
                return new MediaFoundationReader(path);
            case ".ogg":
            case ".opus":
                // Primero el códec nativo de Windows (rápido si está instalado);
                // si falla, decodificamos con Concentus (respaldo lento).
                try { return new MediaFoundationReader(path); }
                catch { return DecodeOpusToWaveStream(path); }
            default:
                return new AudioFileReader(path); // mp3/wav
        }
    }

    /// <summary>Decodifica un OGG/Opus completo a un WaveStream en memoria (48 kHz mono 16-bit).</summary>
    private static WaveStream DecodeOpusToWaveStream(string path)
    {
        const int rate = 48000;
        var decoder = OpusCodecFactory.CreateDecoder(rate, 1);

        using var fileIn = File.OpenRead(path);
        var ogg = new OpusOggReadStream(decoder, fileIn);

        var pcm = new List<short>();
        while (ogg.HasNextPacket)
        {
            short[] packet = ogg.DecodeNextPacket();
            if (packet is { Length: > 0 })
                pcm.AddRange(packet);
        }

        var shorts = pcm.ToArray();
        var bytes = new byte[shorts.Length * sizeof(short)];
        Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
        return new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(rate, 16, 1));
    }

    public void Dispose() => Stop();
}
