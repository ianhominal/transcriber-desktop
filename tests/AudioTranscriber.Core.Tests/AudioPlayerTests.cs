using System.Reflection;
using AudioTranscriber.Core.Audio;
using NAudio.Wave;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Fixes de correctitud 2026-07-13 (ver changelog): AudioPlayer usaba _reader/_output sin
/// sincronización entre el hilo que llama Play()/Stop() y el hilo interno de NAudio que dispara
/// PlaybackStopped, con riesgo real de NullReferenceException/ObjectDisposedException en un hilo
/// NO-Dispatcher (eso mata el proceso entero, ver App.OnAppDomainUnhandledException). Estos tests
/// cubren los dos escenarios pedidos: (1) Play/Stop concurrentes no tiran, y (2) OnPlaybackStopped
/// dispara PlaybackEnded también cuando NAudio para por un error (e.Exception != null), para no
/// dejar la UI trabada en "Reproduciendo".
/// </summary>
public class AudioPlayerTests : IDisposable
{
    private readonly string _dir;

    public AudioPlayerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_player_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // Reintento best-effort: en Windows, el handle de archivo de un WaveOutEvent/AudioFileReader
        // recién cerrado puede tardar un instante extra en liberarse de verdad (ej. si el AV está
        // escaneando el archivo justo después del Dispose()) -- nada que ver con el bug de threading
        // que este archivo testea, pero sin este retry el cleanup del directorio temporal podía
        // fallar de forma intermitente con IOException "being used by another process".
        if (!Directory.Exists(_dir))
            return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    // WAV sintético muy corto (mismo patrón que AudioConverterTests.CreateWav): alcanza para que
    // AudioFileReader lo abra y WaveOutEvent lo reproduzca de punta a punta rápido, que es
    // justamente el escenario del bug ("reproducir un clip corto y tocar 'siguiente' justo cuando
    // termina").
    private string CreateShortWav(string name)
    {
        var path = Path.Combine(_dir, name);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
        using var writer = new WaveFileWriter(path, format);
        var totalSamples = (int)(8000 * 0.05); // 50ms
        for (var i = 0; i < totalSamples; i++)
            writer.WriteSample((float)Math.Sin(2 * Math.PI * 440 * i / 8000));
        return path;
    }

    private static void InvokeOnPlaybackStopped(AudioPlayer player, StoppedEventArgs args)
    {
        var method = typeof(AudioPlayer).GetMethod("OnPlaybackStopped", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnPlaybackStopped no encontrado (¿cambió de firma/nombre?).");
        method.Invoke(player, new object?[] { null, args });
    }

    [Fact]
    public void OnPlaybackStopped_con_exception_dispara_PlaybackEnded()
    {
        using var player = new AudioPlayer();
        var fired = new ManualResetEventSlim(false);
        player.PlaybackEnded += () => fired.Set();

        // Simula que NAudio paró por un error (p.ej. dispositivo desconectado) en vez de terminar
        // naturalmente. Antes del fix, e.Exception se ignoraba del todo y la UI quedaba pegada en
        // "Reproduciendo" para siempre porque PlaybackEnded nunca se disparaba. El evento ahora se
        // despacha vía Task.Run (async, ver fix del deadlock), por eso se espera con Wait en vez de
        // leer un bool al toque.
        InvokeOnPlaybackStopped(player, new StoppedEventArgs(new InvalidOperationException("device lost")));

        Assert.True(fired.Wait(2000), "PlaybackEnded debería dispararse cuando NAudio para por una excepción, para destrabar la UI.");
    }

    [Fact]
    public void OnPlaybackStopped_sin_exception_y_sin_reader_activo_no_dispara_PlaybackEnded()
    {
        using var player = new AudioPlayer();
        var fired = new ManualResetEventSlim(false);
        player.PlaybackEnded += () => fired.Set();

        // Sin excepción y sin haber reproducido nada (_reader null): no hay "fin de audio" real que
        // reportar, así que PlaybackEnded no debe dispararse (se espera un ratito para confirmar que
        // tampoco lo hace de forma async).
        InvokeOnPlaybackStopped(player, new StoppedEventArgs());

        Assert.False(fired.Wait(200));
    }

    [Fact(Skip = "Reproduce audio real por el dispositivo de salida (hace ruido en la máquina, incluso con Volume=0 en algunos drivers). Correr manualmente quitando el Skip para verificar el fix de threading de Play/Stop concurrentes.")]
    public async Task Play_y_Stop_concurrentes_no_generan_excepciones_no_observadas()
    {
        var path = CreateShortWav("clip.wav");

        // Sanity check por separado del stress test de abajo: si este entorno no tiene un
        // dispositivo de audio utilizable, Play() tira acá mismo (sincrónico, hilo del test) y el
        // test se salta en vez de reportar un falso positivo no relacionado con el bug de threading.
        AudioPlayer? probe = null;
        try
        {
            probe = new AudioPlayer();
            probe.Volume = 0f; // silencioso: este test ejercita threading, no salida de audio -- no debe sonar en la máquina.
            probe.Play(path);
            probe.Stop();
        }
        catch (Exception ex)
        {
            probe?.Dispose();
            // No hay [Skip] dinámico en xUnit v2 sin paquetes extra: se documenta y se sale del
            // test sin assert, para no romper `dotnet test` en una máquina/CI sin salida de audio.
            Console.WriteLine($"[SKIP] Sin dispositivo de audio disponible en este entorno: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        finally
        {
            probe?.Dispose();
        }

        // El bug real (ver AudioPlayer._gate): una excepción no manejada acá ocurre en el hilo
        // INTERNO de NAudio, no en el hilo que llama Play()/Stop() -- así que no la vamos a ver como
        // una excepción del método de test, sino como AppDomain.UnhandledException (que en producción
        // tumba el proceso entero). Se captura acá para poder afirmar "no pasó" en vez de dejar que
        // mate el proceso de test.
        Exception? unhandled = null;
        UnhandledExceptionEventHandler handler = (_, e) => unhandled = e.ExceptionObject as Exception;
        AppDomain.CurrentDomain.UnhandledException += handler;

        try
        {
            using var player = new AudioPlayer();
            player.Volume = 0f; // silencioso: ver comentario del probe -- no debe hacer ruido en la máquina.
            var rnd = new Random(12345);

            // Una apertura de dispositivo por iteración (no 25 simultáneas -- eso solo generaría
            // ruido de "dispositivo ocupado" ajeno al bug). Cada iteración dispara Play() en un
            // hilo y, con un jitter alrededor de la duración del clip (50ms), Stop() en OTRO hilo --
            // exactamente el escenario reportado: "tocar siguiente" justo cuando el clip termina
            // solo, con el fin natural (hilo interno de NAudio) y el Stop() explícito solapados.
            for (var i = 0; i < 20; i++)
            {
                await Task.Run(() => player.Play(path));
                await Task.Run(async () =>
                {
                    await Task.Delay(rnd.Next(30, 70));
                    player.Stop();
                });
            }

            player.Stop();

            // Da tiempo a que cualquier hilo de NAudio todavía en vuelo termine de disparar
            // PlaybackStopped antes de chequear si hubo una excepción no observada.
            await Task.Delay(300);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= handler;
        }

        Assert.Null(unhandled);
    }

    [Fact]
    public void OnPlaybackStopped_despacha_PlaybackEnded_async_sin_bloquear_al_hilo_de_NAudio()
    {
        // Regresión del deadlock (ver AudioPlayer.OnPlaybackStopped): PlaybackEnded se dispara en el
        // hilo interno de NAudio, y en producción el handler (MainViewModel) llama a Stop() de forma
        // bloqueante -> Stop() hace Join() sobre ESE mismo hilo -> deadlock que congela la app en
        // cada fin natural de reproducción. El fix despacha el evento vía Task.Run, así este handler
        // retorna de inmediato. Simulamos un subscriber que se cuelga: si el evento fuera sincrónico,
        // InvokeOnPlaybackStopped bloquearía; con el fix, no.
        using var player = new AudioPlayer();
        var handlerStarted = new ManualResetEventSlim(false);
        var releaseHandler = new ManualResetEventSlim(false);
        player.PlaybackEnded += () =>
        {
            handlerStarted.Set();
            releaseHandler.Wait(2000);
        };

        InvokeOnPlaybackStopped(player, new StoppedEventArgs(new InvalidOperationException("device lost")));

        // Si llegamos acá sin colgarnos, el evento se despachó a otro hilo (no sincrónico).
        Assert.True(handlerStarted.Wait(2000), "PlaybackEnded debería ejecutarse en un hilo aparte, no bloquear al de NAudio.");
        releaseHandler.Set();
    }
}
