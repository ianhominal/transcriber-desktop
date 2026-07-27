using System.Windows;
using System.Windows.Threading;
using AudioTranscriber.App.ViewModels;
using AudioTranscriber.Core.Meetings;

namespace AudioTranscriber.App;

/// <summary>
/// Orquesta la detección automática de reuniones (Meet/Zoom/Teams/Discord voz): pollea
/// <see cref="MicrophoneUsageReader"/> (I/O sobre el registro) cada <see cref="PollInterval"/>, le
/// pasa el snapshot a <see cref="MeetingDetector"/> (Core, puro y testeado -- toda la lógica de
/// "¿esto es una reunión? ¿arrancó/terminó?" vive ahí, ver sus tests) y, según
/// <see cref="AppSettings.MeetingDetection"/>, ofrece o arranca sola la grabación de reunión --
/// SIEMPRE reusando <see cref="MainViewModel.ToggleMeetingRecordingCommand"/>/
/// <see cref="MainViewModel.AutoStopMeetingRecordingAsync"/> (que a su vez reusan
/// <c>MeetingRecorder</c>), nunca reinventando la captura.
/// <para/>
/// Esta clase es deliberadamente NO testeada por unidad (a diferencia de <see cref="MeetingDetector"/>,
/// que sí lo está con TDD real): es pura plomería de I/O + WPF (registro, <see cref="Application.MainWindow"/>,
/// notificación de bandeja) sin lógica de decisión propia -- esa lógica está en Core.
/// Instancia única (<see cref="Instance"/>), mismo patrón que <see cref="UpdateService"/>/
/// <see cref="SyncCoordinator"/>.
/// </summary>
public sealed class MeetingDetectionService
{
    public static MeetingDetectionService Instance { get; } = new();

    /// <summary>Cada cuánto se relee el registro. Leer el ConsentStore es liviano, así que un
    /// intervalo corto no pesa; 4s da una respuesta rápida sin generar carga perceptible.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Snapshots seguidos en el mismo sentido para confirmar una transición (debounce, ver
    /// <see cref="MeetingDetector"/>): con <see cref="PollInterval"/>=4s, 2 snapshots ≈ 8s -- un
    /// blip de un tick (Windows tarda un instante en escribir el registro, alguien suelta el mic un
    /// segundo) no alcanza para disparar un prompt o una auto-grabación de mentira.
    /// </summary>
    private const int DebounceSnapshots = 2;

    private readonly DispatcherTimer _timer;
    private readonly MeetingDetector _detector = new(DebounceSnapshots);

    /// <summary>
    /// True si LA grabación de reunión en curso la arrancó esta detección (auto o vía el prompt
    /// "¿Grabar?" -- las dos cuentan igual, ver el brief). Si el usuario grabó a mano, esto queda
    /// false y el fin de la reunión NO le toca la grabación -- ver <see cref="OnMeetingEnded"/>.
    /// Se auto-corrige en cada <see cref="Poll"/> si la grabación que arrancamos ya no está en
    /// curso (el usuario la frenó a mano antes de que la detección viera el fin de la reunión):
    /// así un "terminó" tardío del detector nunca llega a tocar una grabación manual nueva que el
    /// usuario haya arrancado después.
    /// </summary>
    private bool _recordingStartedByDetection;

    private MeetingDetectionService()
    {
        // DispatcherTimer: primer acceso a Instance siempre desde App.OnStartup (hilo de UI, ver
        // Start()), mismo criterio que UpdateService._periodicCheckTimer -- el Tick ya cae en ese
        // hilo sin marshaling manual.
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Arranca el poll. Llamado una sola vez desde App.OnStartup. Idempotente.</summary>
    public void Start() => _timer.Start();

    /// <summary>Detiene el poll (ver App.OnExit). Idempotente.</summary>
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        // "Desactivado": ni siquiera se lee el registro -- no hay nada que decidir (ver el brief).
        if (string.Equals(AppSettings.Instance.MeetingDetection, "Desactivado", StringComparison.OrdinalIgnoreCase))
            return;

        var vm = Application.Current?.MainWindow?.DataContext as MainViewModel;

        // Auto-corrección del flag -- ver su XML doc.
        if (_recordingStartedByDetection && vm is not null && !vm.IsRecording)
            _recordingStartedByDetection = false;

        var micUsingApps = MicrophoneUsageReader.GetAppsUsingMicrophoneNow();
        var ownIdentifier = Environment.ProcessPath ?? string.Empty;
        var transition = _detector.Update(micUsingApps, ownIdentifier);

        switch (transition)
        {
            case MeetingTransition.Started:
                OnMeetingStarted(vm);
                break;
            case MeetingTransition.Ended:
                OnMeetingEnded(vm);
                break;
        }
    }

    private void OnMeetingStarted(MainViewModel? vm)
    {
        if (vm is null || vm.IsRecording)
            return; // sin ventana principal, o ya hay algo grabando (manual, u otro disparo): no pisamos nada.

        if (string.Equals(AppSettings.Instance.MeetingDetection, "Automatico", StringComparison.OrdinalIgnoreCase))
        {
            vm.ToggleMeetingRecordingCommand.Execute(null);
            _recordingStartedByDetection = true;
            TrayIconService.Current?.NotifyMeetingDetected(
                "Reunión detectada",
                "Grabando la reunión automáticamente…",
                onClick: null);
            return;
        }

        // "Preguntar" (default) -- cualquier valor desconocido/corrupto cae acá también, mismo
        // criterio permisivo que ThemeResolver.Parse con Theme.
        TrayIconService.Current?.NotifyMeetingDetected(
            "Reunión detectada",
            "Detecté una reunión. Click acá para grabarla.",
            onClick: () =>
            {
                if (Application.Current?.MainWindow?.DataContext is MainViewModel currentVm && !currentVm.IsRecording)
                {
                    currentVm.ToggleMeetingRecordingCommand.Execute(null);
                    _recordingStartedByDetection = true;
                }
            });
    }

    private void OnMeetingEnded(MainViewModel? vm)
    {
        if (!_recordingStartedByDetection)
            return;

        _recordingStartedByDetection = false;

        if (vm is { IsRecording: true, IsMeetingRecording: true })
            _ = vm.AutoStopMeetingRecordingAsync();
    }
}
