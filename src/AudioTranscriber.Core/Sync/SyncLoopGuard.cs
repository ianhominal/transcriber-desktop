namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Freno anti-loop del sync automático: el propio <c>SyncEngine</c> escribe archivos (baja
/// proyectos/transcripciones, mueve cosas a <c>.papelera</c>, actualiza <c>.synccache</c>), y esas
/// escrituras también disparan el <c>FileSystemWatcher</c>. Sin este freno, cada sync
/// dispararía eventos que programarían otro sync (y así de nuevo).
/// <para/>
/// Lógica pura: mientras haya un sync en curso (<see cref="BeginSync"/> sin su <see cref="EndSync"/>)
/// se ignoran los eventos, y además se ignoran los que lleguen dentro de una "ventana de
/// asentamiento" después de terminar (los eventos del filesystem pueden llegar con latencia).
/// No depende de reloj real: el llamador pasa <c>now</c> explícito, para poder testear sin esperas.
/// </summary>
public sealed class SyncLoopGuard
{
    private readonly TimeSpan _settleWindow;
    private bool _syncing;
    private DateTimeOffset _suppressUntil = DateTimeOffset.MinValue;

    public SyncLoopGuard(TimeSpan settleWindow)
    {
        if (settleWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(settleWindow), "La ventana no puede ser negativa.");
        _settleWindow = settleWindow;
    }

    /// <summary>Marca el inicio de un ciclo de sync: desde acá, todos los eventos se ignoran.</summary>
    public void BeginSync() => _syncing = true;

    /// <summary>
    /// Marca el fin del ciclo. Los eventos que lleguen antes de <c>now + settleWindow</c> se van a
    /// seguir ignorando (son casi seguro ecos de las escrituras que hizo el propio sync).
    /// </summary>
    public void EndSync(DateTimeOffset now)
    {
        _syncing = false;
        _suppressUntil = now + _settleWindow;
    }

    /// <summary>True si un evento de filesystem a <paramref name="now"/> debería ignorarse.</summary>
    public bool ShouldIgnoreEvent(DateTimeOffset now) => _syncing || now < _suppressUntil;
}
