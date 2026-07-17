using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ComIAudioClient = NAudio.CoreAudioApi.Interfaces.IAudioClient;
using NAudioActivateHandler = NAudio.Wasapi.CoreAudioApi.Interfaces.IActivateAudioInterfaceCompletionHandler;
using NAudioActivateOperation = NAudio.Wasapi.CoreAudioApi.Interfaces.IActivateAudioInterfaceAsyncOperation;

namespace AudioTranscriber.Core.Audio;

/// <summary>
/// Captura el audio que reproduce UNA aplicación puntual (y sus procesos hijos -- Chrome/Edge
/// usan un proceso por pestaña) en vez de todo lo que suena en la PC. Existe para que "Grabar
/// reunión" pueda elegir, por ejemplo, "solo el navegador con el Meet" sin la música de Spotify o
/// una notificación de Windows sonando al mismo tiempo (ver <see cref="MeetingRecorder"/>, que usa
/// esta clase con una RED DE SEGURIDAD: si algo acá falla, cae a todo el sistema).
///
/// INTEROP A MANO -- por qué hace falta: la API de Windows para esto ("process loopback") se pide
/// con <c>ActivateAudioInterfaceAsync</c> pasándole un struct (<c>AUDIOCLIENT_ACTIVATION_PARAMS</c>)
/// que NAudio 2.3.0 NO expone públicamente -- se verificó con reflection sobre el paquete instalado
/// (<c>NAudio.CoreAudioApi.AudioClientActivationType</c>/<c>ProcessLoopbackMode</c> existen pero son
/// <c>internal</c>, y no hay ningún struct público equivalente a <c>AUDIOCLIENT_ACTIVATION_PARAMS</c>
/// en el paquete). Por eso ese struct se define acá a mano, con el layout EXACTO de la documentación
/// oficial. Todo lo DEMÁS -- activar la interfaz una vez armado el pedido, y leer los paquetes de
/// audio ya activados -- reusa las clases PÚBLICAS de NAudio (<see cref="AudioClient"/> y
/// <see cref="AudioCaptureClient"/>, más las interfaces COM públicas
/// <c>NAudio.CoreAudioApi.Interfaces.IAudioClient</c> e
/// <c>IActivateAudioInterfaceCompletionHandler</c>/<c>IActivateAudioInterfaceAsyncOperation</c> de
/// <c>NAudio.Wasapi.CoreAudioApi.Interfaces</c>, también públicas) con el MISMO patrón que
/// <c>NAudio.Wave.WasapiCapture</c> usa internamente para el loopback normal: hilo de captura
/// dedicado + evento de Windows + <c>GetBuffer</c>/<c>ReleaseBuffer</c>. No hay reimplementación de
/// WASAPI: solo del paso de ACTIVACIÓN, que es la única pieza que falta en el paquete.
///
/// Fuentes usadas para el layout exacto de cada struct/enum/constante (verificado contra TRES
/// fuentes independientes que coinciden byte a byte entre sí):
/// 1. Documentación oficial de cada tipo:
///    - https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_activation_params
///    - https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params
///    - https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-audioclient_activation_type
///    - https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-process_loopback_mode
///    - https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync
///      (firma de ActivateAudioInterfaceAsync, exportada por Mmdevapi.dll)
/// 2. El ejemplo OFICIAL de Microsoft en C++ (confirma el uso real: el device id
///    VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, el PROPVARIANT tipo VT_BLOB envolviendo el struct de
///    activación, las flags de Initialize -- LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM -- y que acá
///    el WAVEFORMATEX hay que pedirlo EXPLÍCITO -- PCM 44100 Hz / 16 bit / estéreo -- porque
///    GetMixFormat en este dispositivo virtual devuelve E_NOTIMPL, a diferencia del loopback normal
///    que sí puede preguntarle su formato nativo al dispositivo real):
///    https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/ApplicationLoopback/cpp/LoopbackCapture.cpp
/// 3. Una reimplementación en C# real (todavía sin publicar) del propio equipo de NAudio, que
///    coincide EXACTO con lo anterior -- confirma el layout de los structs ya traducidos a C#, el
///    valor del device id ("VAD\Process_Loopback") y el IID de IAudioClient que ya venía usando
///    NAudio 2.3.0 (1CB9AD4C-DBFA-4c32-B178-C2F568A703B2, verificado también por reflection sobre
///    el paquete instalado):
///    https://github.com/naudio/NAudio/blob/61babaf869ca527e0b6ebb8ac1bbadd8396a6f8f/src/NAudio.Wasapi/CoreAudioApi/AudioClientStreamFlags.cs
///    https://github.com/naudio/NAudio/blob/61babaf869ca527e0b6ebb8ac1bbadd8396a6f8f/src/NAudio.Wasapi/CoreAudioApi/AudioClient.cs
/// </summary>
internal sealed class ProcessLoopbackCapture : IWaveIn
{
    // ---- AUDIOCLIENT_ACTIVATION_PARAMS y compañía (fuentes 1-3 arriba) -----------------------

    /// <summary>AUDIOCLIENT_ACTIVATION_TYPE.</summary>
    private enum ActivationType
    {
        Default = 0,
        ProcessLoopback = 1,
    }

    /// <summary>PROCESS_LOOPBACK_MODE.</summary>
    private enum LoopbackMode
    {
        /// <summary>Incluye el proceso pedido Y sus hijos -- Chrome/Edge abren un proceso por pestaña.</summary>
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1,
    }

    /// <summary>AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS -- DWORD + enum de 4 bytes, sin padding.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessLoopbackParams
    {
        public uint TargetProcessId;
        public LoopbackMode Mode;
    }

    /// <summary>AUDIOCLIENT_ACTIVATION_PARAMS -- enum de 4 bytes + el struct de arriba (la unión de
    /// la doc solo tiene UN miembro posible hoy, así que es directamente el struct, sin wrapper).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public ActivationType Type;
        public ProcessLoopbackParams ProcessLoopbackParams;
    }

    /// <summary>
    /// Cabecera de un PROPVARIANT tipo VT_BLOB (wtypes.h): 2 bytes de tipo + 6 bytes de relleno
    /// (para que el campo de 4 bytes que sigue quede alineado, tal cual layout real de PROPVARIANT)
    /// + tamaño del blob + puntero a los datos. Es el mecanismo que exige
    /// ActivateAudioInterfaceAsync para pasar <see cref="ActivationParams"/> como "activationParams".
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    /// <summary>VT_BLOB (wtypes.h, enum VARENUM) = 65 (0x41).</summary>
    private const ushort VT_BLOB = 65;

    /// <summary>Device id especial que le dice a ActivateAudioInterfaceAsync "no un dispositivo
    /// real: quiero el loopback de UN PROCESO puntual" (ver fuente 3 en el comentario de clase).</summary>
    private const string VirtualAudioDeviceProcessLoopback = @"VAD\Process_Loopback";

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        NAudioActivateHandler completionHandler,
        out NAudioActivateOperation activationOperation);

    /// <summary>
    /// Marcador COM (sin métodos) que le dice a Windows "este objeto se puede llamar desde
    /// cualquier hilo/apartamento sin pasar por un proxy" -- IID_IAgileObject, constante fija de
    /// Windows (objidl.h). Sin esto, el callback de <see cref="ActivateAudioInterfaceAsync"/> --
    /// que Windows dispara desde un hilo de fondo (MTA) -- podría necesitar que ESTE hilo (el de
    /// UI, STA) esté bombeando mensajes para completarse, y acá lo estamos bloqueando con un
    /// <see cref="ManualResetEventSlim.Wait(TimeSpan)"/> liso, sin bombear nada: sin el marcador,
    /// eso podría colgarse en vez de completar. Ver la doc de ActivateAudioInterfaceAsync (fuente 1).
    /// </summary>
    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    private sealed class ActivationHandler : NAudioActivateHandler, IAgileObject
    {
        private readonly ManualResetEventSlim _completed = new(initialState: false);

        // E_FAIL hasta que ActivateCompleted corra -- si Wait() da timeout, éste es el HR que
        // ActivateForProcess reporta.
        public int ResultHr { get; private set; } = unchecked((int)0x80004005);
        public object? ActivatedInterface { get; private set; }

        public void ActivateCompleted(NAudioActivateOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var hr, out var iface);
                ResultHr = hr;
                ActivatedInterface = iface;
            }
            finally
            {
                _completed.Set();
            }
        }

        public bool WaitForCompletion(TimeSpan timeout) => _completed.Wait(timeout);
    }

    /// <summary>
    /// Activa la captura del audio de <paramref name="processId"/> (y sus hijos) y la deja lista
    /// para <see cref="StartRecording"/>. Tira si CUALQUIER paso falla -- Windows viejo, la app se
    /// cerró justo antes, activación rechazada, lo que sea -- y es responsabilidad de quien llama
    /// (<see cref="MeetingRecorder"/>) decidir qué hacer con eso (ver la RED DE SEGURIDAD ahí:
    /// nunca se pierde la grabación por esto).
    /// </summary>
    public static ProcessLoopbackCapture ActivateForProcess(int processId, TimeSpan timeout)
    {
        var audioClient = Activate((uint)processId, timeout);
        try
        {
            // Formato pedido EXPLÍCITO -- ver el punto 2 del comentario de clase sobre por qué
            // (GetMixFormat no sirve acá). AutoConvertPcm es obligatorio con un formato propio en
            // modo compartido: sin esa flag, Initialize fallaría si no coincide con el formato
            // interno del motor de audio.
            var waveFormat = new WaveFormat(44100, 16, 2);
            audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                0,
                0,
                waveFormat,
                Guid.Empty);

            return new ProcessLoopbackCapture(audioClient, waveFormat);
        }
        catch
        {
            audioClient.Dispose();
            throw;
        }
    }

    private static AudioClient Activate(uint processId, TimeSpan timeout)
    {
        var activationParams = new ActivationParams
        {
            Type = ActivationType.ProcessLoopback,
            ProcessLoopbackParams = new ProcessLoopbackParams
            {
                TargetProcessId = processId,
                Mode = LoopbackMode.IncludeTargetProcessTree,
            },
        };

        int paramsSize = Marshal.SizeOf<ActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr blobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        NAudioActivateOperation? operation = null;
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, fDeleteOld: false);
            var blob = new PropVariantBlob
            {
                VarType = VT_BLOB,
                BlobSize = (uint)paramsSize,
                BlobData = paramsPtr,
            };
            Marshal.StructureToPtr(blob, blobPtr, fDeleteOld: false);

            var handler = new ActivationHandler();
            var riid = typeof(ComIAudioClient).GUID;
            ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, riid, blobPtr, handler, out operation);

            // La memoria de activationParams/blob tiene que seguir viva hasta que el callback
            // de activación termine de leerla -- por eso el Wait() (y todo lo que depende de él)
            // queda DENTRO de este try, antes del finally que la libera. Ver la fuente 3 del
            // comentario de clase: la propia reimplementación de NAudio libera esta memoria recién
            // después de esperar el resultado, no apenas vuelve la llamada nativa (que es
            // asincrónica: solo encola el pedido).
            if (!handler.WaitForCompletion(timeout))
                throw new TimeoutException("La aplicación elegida no contestó a tiempo al pedido de captura de audio.");

            Marshal.ThrowExceptionForHR(handler.ResultHr);

            if (handler.ActivatedInterface is not ComIAudioClient iAudioClient)
                throw new InvalidOperationException("No se pudo activar la captura de audio de esa aplicación.");

            return new AudioClient(iAudioClient);
        }
        finally
        {
            if (operation is not null)
            {
                // ReleaseComObject es Windows-only (CA1416) -- igual que TODO este archivo (COM/
                // WASAPI), pero el proyecto Core apunta a "net8.0" liso, no "net8.0-windows" (ver
                // MMDeviceEnumerator/WasapiLoopbackCapture en MeetingRecorder, que ya hacen lo
                // mismo sin marcar la clase): se suprime puntual acá en vez de reetiquetar todo el
                // proyecto para una app que de todas formas solo corre en Windows.
#pragma warning disable CA1416
                Marshal.ReleaseComObject(operation);
#pragma warning restore CA1416
            }
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    // ---- Captura propiamente dicha -- mismo patrón que NAudio.Wave.WasapiCapture (hilo propio +
    // evento + GetBuffer/ReleaseBuffer), simplificado: acá siempre es modo compartido + evento. ----

    private const long ReftimesPerSec = 10_000_000;
    private const long ReftimesPerMillisec = 10_000;

    private readonly AudioClient _audioClient;
    private readonly int _bytesPerFrame;
    private EventWaitHandle? _frameEvent;
    private Thread? _captureThread;
    private volatile bool _stopRequested;
    private byte[] _recordBuffer = Array.Empty<byte>();

    public WaveFormat WaveFormat { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    private ProcessLoopbackCapture(AudioClient audioClient, WaveFormat waveFormat)
    {
        _audioClient = audioClient;
        WaveFormat = waveFormat;
        _bytesPerFrame = waveFormat.Channels * waveFormat.BitsPerSample / 8;
    }

    public void StartRecording()
    {
        _stopRequested = false;
        _frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _audioClient.SetEventHandle(_frameEvent.SafeWaitHandle.DangerousGetHandle());

        int bufferFrameCount = _audioClient.BufferSize;
        _recordBuffer = new byte[Math.Max(bufferFrameCount, 1) * _bytesPerFrame];

        _captureThread = new Thread(CaptureThreadProc) { IsBackground = true };
        _captureThread.Start();
    }

    public void StopRecording() => _stopRequested = true;

    private void CaptureThreadProc()
    {
        Exception? failure = null;
        try
        {
            DoCapture();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            try { _audioClient.Stop(); }
            catch { /* ya se estaba deteniendo -- no hay nada más para hacer acá. */ }
        }
        RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
    }

    private void DoCapture()
    {
        int bufferFrameCount = _audioClient.BufferSize;
        long actualDuration = (long)((double)ReftimesPerSec * bufferFrameCount / WaveFormat.SampleRate);
        int waitMilliseconds = (int)Math.Max(1, 3 * actualDuration / ReftimesPerMillisec);

        var capture = _audioClient.AudioCaptureClient;
        _audioClient.Start();

        while (!_stopRequested)
        {
            _frameEvent!.WaitOne(waitMilliseconds);
            if (_stopRequested)
                break;
            ReadNextPacket(capture);
        }
    }

    private void ReadNextPacket(AudioCaptureClient capture)
    {
        int packetSize = capture.GetNextPacketSize();
        int offset = 0;

        while (packetSize != 0)
        {
            IntPtr buffer = capture.GetBuffer(out int framesAvailable, out AudioClientBufferFlags flags);
            int bytesAvailable = framesAvailable * _bytesPerFrame;

            int spaceRemaining = Math.Max(0, _recordBuffer.Length - offset);
            if (spaceRemaining < bytesAvailable && offset > 0)
            {
                DataAvailable?.Invoke(this, new WaveInEventArgs(_recordBuffer, offset));
                offset = 0;
            }
            // Colchón extra sobre lo que hace WasapiCapture: si un solo paquete no entra NI
            // vaciando el buffer, se agranda en vez de tirar (Marshal.Copy tiraría si el destino
            // queda chico).
            if (_recordBuffer.Length < bytesAvailable)
                _recordBuffer = new byte[bytesAvailable];

            if ((flags & AudioClientBufferFlags.Silent) != AudioClientBufferFlags.Silent)
                Marshal.Copy(buffer, _recordBuffer, offset, bytesAvailable);
            else
                Array.Clear(_recordBuffer, offset, bytesAvailable);

            offset += bytesAvailable;
            capture.ReleaseBuffer(framesAvailable);
            packetSize = capture.GetNextPacketSize();
        }
        DataAvailable?.Invoke(this, new WaveInEventArgs(_recordBuffer, offset));
    }

    public void Dispose()
    {
        StopRecording();
        _captureThread?.Join();
        _captureThread = null;
        _frameEvent?.Dispose();
        _frameEvent = null;
        _audioClient.Dispose();
    }
}
