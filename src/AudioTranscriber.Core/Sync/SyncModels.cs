namespace AudioTranscriber.Core.Sync;

/// <summary>Tipo de entidad que se sincroniza.</summary>
public enum SyncItemKind
{
    Project,
    Transcription,
}

/// <summary>Acción resultante de reconciliar los tres estados (base, local, remoto).</summary>
public enum SyncActionType
{
    PushUpsert,   // el cambio local viaja al servidor (crear/actualizar)
    PushDelete,   // el borrado local viaja al servidor
    PullUpsert,   // el cambio remoto se aplica en local
    PullDelete,   // el borrado remoto se aplica en local
}

/// <summary>
/// Estado de un item en UN endpoint (local o remoto). Se compara por (ContentHash, Deleted);
/// UpdatedAt se usa para resolver conflictos (last-write-wins). <see cref="ContentHash"/> vive en
/// el espacio de hash DE ESE LADO -- <see cref="LocalScanner"/> y <see cref="RemoteMapper"/> lo
/// calculan sobre campos DISJUNTOS (ver comentarios en cada uno), así que NUNCA hay que comparar
/// el <see cref="ContentHash"/> de un local contra el de un remoto directamente (ver
/// <see cref="SyncBaselineItem"/>, que es quien ancla ambos espacios por separado).
/// </summary>
public sealed record SyncItemState(
    string Id,
    SyncItemKind Kind,
    string ContentHash,
    DateTimeOffset UpdatedAt,
    bool Deleted = false);

/// <summary>
/// Estado persistido en la baseline (el "último sync exitoso") para un item. A diferencia de
/// <see cref="SyncItemState"/> -- que representa el hash de UN SOLO lado, local O remoto --, la
/// baseline necesita anclar AMBOS espacios de hash por separado, porque local y remoto calculan su
/// ContentHash sobre campos DISJUNTOS (ver <see cref="LocalScanner.ScanDetailed"/> y
/// <see cref="RemoteMapper.Map"/>).
/// <para/>
/// Bugfix 2026-07-10 (oscilación perpetua de sync, ver changelog): antes la baseline guardaba UN
/// solo <c>ContentHash</c> (heredado del lado que "ganó" la última acción -- local tras un push,
/// remoto tras un pull) y <see cref="SyncPlanner"/> lo comparaba contra CUALQUIERA de los dos lados
/// según tocara. Como los dos espacios de hash nunca coinciden por diseño, esa comparación cruzada
/// no tenía punto fijo: cada ciclo alternaba PushUpsert/PullUpsert para siempre aunque nada hubiera
/// cambiado de verdad. <see cref="LastLocalHash"/>/<see cref="LastRemoteHash"/> son el ÚLTIMO hash
/// CONOCIDO de cada lado tal como quedaron sincronizados; <see cref="SyncPlanner"/> compara cada
/// réplica SOLO contra su propio espacio -- nunca local contra remoto.
/// </summary>
public sealed record SyncBaselineItem(
    string Id,
    SyncItemKind Kind,
    string LastLocalHash,
    string LastRemoteHash,
    DateTimeOffset UpdatedAt,
    bool Deleted = false);

/// <summary>Operación concreta a ejecutar tras reconciliar.</summary>
public sealed record SyncAction(
    string Id,
    SyncItemKind Kind,
    SyncActionType Type,
    string Reason = "");
