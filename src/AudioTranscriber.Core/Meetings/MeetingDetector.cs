namespace AudioTranscriber.Core.Meetings;

/// <summary>
/// Transición pura que devuelve <see cref="MeetingDetector.Update"/> en cada snapshot: si no cambió
/// nada (<see cref="None"/>) o si recién ahora se confirmó que arrancó/terminó una reunión. Se
/// dispara UNA sola vez por cambio real de estado -- ver el comentario de clase de
/// <see cref="MeetingDetector"/>.
/// </summary>
public enum MeetingTransition
{
    None,
    Started,
    Ended,
}

/// <summary>
/// Máquina de estados PURA que decide "hay una reunión en curso" a partir de qué apps están usando
/// el micrófono ahora mismo. Sin registro, sin I/O, sin timers -- 100% testeable con listas de
/// strings a mano (ver <see cref="AudioTranscriber.Core.Tests.MeetingDetectorTests"/>).
/// <para/>
/// La señal real (leer el registro ConsentStore de Windows para saber quién tiene el mic abierto
/// AHORA MISMO) vive en la capa App (ver <c>MicrophoneUsageReader</c>/<c>MeetingDetectionService</c>,
/// que pollean el registro cada pocos segundos y le pasan acá el snapshot). Esta clase solo sabe:
/// (1) matchear un identificador de proceso contra patrones conocidos de apps de reunión
/// (<see cref="MatchesMeetingApp"/>), (2) excluir el propio proceso (para no auto-dispararse
/// mientras esta misma app está grabando y por eso usa el mic), y (3) confirmar una transición
/// SOLO tras <see cref="_requiredConsecutiveSnapshots"/> snapshots seguidos en el mismo sentido
/// (debounce): un blip de un tick -- Windows tarda en escribir el registro, alguien suelta el mic
/// un segundo -- no alcanza para disparar un prompt o una auto-grabación de mentira.
/// </summary>
public sealed class MeetingDetector
{
    /// <summary>
    /// Patrones (case-insensitive, substring) de procesos/apps que sabemos que llevan reuniones:
    /// navegadores (Meet corre adentro de uno) + las apps nativas de Zoom/Teams/Discord. Ojo: esto
    /// matchea CUALQUIER navegador con el mic abierto, no solo uno con un Meet realmente abierto --
    /// es la señal más simple y suficiente que pide el brief (cruzar con el título de la ventana
    /// queda fuera de este alcance).
    /// </summary>
    private static readonly string[] MeetingAppPatterns =
    {
        "chrome", "msedge", "firefox", "brave", "opera",
        "zoom", "teams", "ms-teams", "discord",
    };

    private readonly int _requiredConsecutiveSnapshots;
    private int _consecutiveMatching;
    private int _consecutiveNotMatching;

    /// <param name="requiredConsecutiveSnapshots">
    /// Cuántos snapshots SEGUIDOS en el mismo sentido hacen falta para confirmar una transición
    /// (debounce). 1 (default) = sin debounce, cada snapshot decide al toque -- útil para tests. La
    /// capa App (poll cada pocos segundos) pasa un valor mayor a 1 en producción.
    /// </param>
    public MeetingDetector(int requiredConsecutiveSnapshots = 1)
    {
        if (requiredConsecutiveSnapshots < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredConsecutiveSnapshots),
                "Tiene que ser al menos 1.");
        _requiredConsecutiveSnapshots = requiredConsecutiveSnapshots;
    }

    /// <summary>True si, según el último snapshot confirmado (post-debounce), hay una reunión en curso.</summary>
    public bool InMeeting { get; private set; }

    /// <summary>
    /// Procesa un snapshot nuevo: quiénes están usando el micrófono ahora mismo
    /// (<paramref name="micUsingApps"/> -- identificadores de proceso/ruta, lo que sea que devuelva
    /// el registro), excluyendo <paramref name="ownProcessIdentifier"/> (regla (b) del brief: solo
    /// el propio proceso usando el mic -- porque ESTA app está grabando -- no cuenta como reunión).
    /// Devuelve la transición pura resultante (ver <see cref="MeetingTransition"/>).
    /// </summary>
    public MeetingTransition Update(IEnumerable<string> micUsingApps, string ownProcessIdentifier)
    {
        bool matchesNow = micUsingApps.Any(id =>
            !string.Equals(id, ownProcessIdentifier, StringComparison.OrdinalIgnoreCase) &&
            MatchesMeetingApp(id));

        if (matchesNow)
        {
            _consecutiveMatching++;
            _consecutiveNotMatching = 0;
        }
        else
        {
            _consecutiveNotMatching++;
            _consecutiveMatching = 0;
        }

        if (!InMeeting && _consecutiveMatching >= _requiredConsecutiveSnapshots)
        {
            InMeeting = true;
            return MeetingTransition.Started;
        }

        if (InMeeting && _consecutiveNotMatching >= _requiredConsecutiveSnapshots)
        {
            InMeeting = false;
            return MeetingTransition.Ended;
        }

        return MeetingTransition.None;
    }

    /// <summary>
    /// True si <paramref name="identifier"/> (nombre de proceso o ruta completa) matchea alguno de
    /// los patrones conocidos de apps de reunión (<see cref="MeetingAppPatterns"/>).
    /// Case-insensitive, substring -- así matchea tanto "chrome" como
    /// "C:\...\chrome.exe" o "GoogleChrome.exe".
    /// </summary>
    public static bool MatchesMeetingApp(string identifier) =>
        !string.IsNullOrWhiteSpace(identifier) &&
        MeetingAppPatterns.Any(p => identifier.Contains(p, StringComparison.OrdinalIgnoreCase));
}
