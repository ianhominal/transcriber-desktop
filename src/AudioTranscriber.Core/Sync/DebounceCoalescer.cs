namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Lógica pura de debounce/coalescing para ráfagas de eventos (p.ej. un <c>FileSystemWatcher</c>
/// disparando decenas de eventos al copiar varios archivos): cada <see cref="Signal"/> reprograma
/// el "vencimiento" a <c>ahora + delay</c>, así que una ráfaga de eventos termina disparando una
/// sola vez, <paramref name="delay"/> después del ÚLTIMO evento.
/// No usa timers reales (ni <c>DispatcherTimer</c> ni <c>System.Threading.Timer</c>): el llamador
/// le pasa la hora actual explícitamente (<see cref="TryConsumeDue"/>), lo que lo hace 100%
/// determinístico y rápido de testear, sin esperas reales ni flakiness.
/// </summary>
public sealed class DebounceCoalescer
{
    private readonly TimeSpan _delay;
    private DateTimeOffset? _dueAt;

    public DebounceCoalescer(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), "El delay debe ser positivo.");
        _delay = delay;
    }

    /// <summary>True mientras hay una ejecución pendiente (todavía no vencida ni consumida).</summary>
    public bool HasPending => _dueAt is not null;

    /// <summary>Registra un evento a <paramref name="now"/>, reprogramando el vencimiento.</summary>
    public void Signal(DateTimeOffset now) => _dueAt = now + _delay;

    /// <summary>
    /// Si hay un vencimiento pendiente y ya se cumplió (<paramref name="now"/> &gt;= vencimiento),
    /// lo consume (vuelve a quedar sin pendientes) y devuelve true. Si no hay nada pendiente, o
    /// todavía no venció, devuelve false sin tocar el estado.
    /// </summary>
    public bool TryConsumeDue(DateTimeOffset now)
    {
        if (_dueAt is null || now < _dueAt.Value)
            return false;
        _dueAt = null;
        return true;
    }
}
