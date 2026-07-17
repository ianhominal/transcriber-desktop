namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Reconciliación de tres vías (base / local / remoto) para sync bidireccional, al estilo
/// Dropbox/Syncthing: se compara cada endpoint contra el estado del último sync (base) para
/// saber qué cambió de cada lado. Conflictos por last-write-wins (UpdatedAt).
///
/// Principio de seguridad: la AUSENCIA de un item en un snapshot no significa "borrado"
/// (podría ser "todavía no sincronizado"). Los borrados llegan como tombstones (Deleted=true).
/// </summary>
public sealed class SyncPlanner
{
    public IReadOnlyList<SyncAction> Plan(
        IReadOnlyDictionary<string, SyncBaselineItem> baseline,
        IReadOnlyDictionary<string, SyncItemState> local,
        IReadOnlyDictionary<string, SyncItemState> remote)
    {
        var actions = new List<SyncAction>();

        var ids = new HashSet<string>(baseline.Keys);
        ids.UnionWith(local.Keys);
        ids.UnionWith(remote.Keys);

        foreach (var id in ids)
        {
            baseline.TryGetValue(id, out var b);

            // Ausencia = sin cambios respecto a base (no se interpreta como borrado). El fallback
            // proyecta la baseline al espacio de hash correspondiente (local o remoto) para que la
            // auto-comparación de abajo dé "sin cambios" -- NUNCA se cruzan espacios de hash.
            var l = local.TryGetValue(id, out var lv) ? lv : AsLocalFallback(b);
            var r = remote.TryGetValue(id, out var rv) ? rv : AsRemoteFallback(b);

            if (l is null && r is null) continue;

            // Bugfix 2026-07-10 (oscilación perpetua de sync): local se compara SOLO contra
            // baseline.LastLocalHash, remoto SOLO contra baseline.LastRemoteHash -- nunca un hash
            // local contra uno remoto (ver comentario largo en SyncBaselineItem). Antes se comparaba
            // un único ContentHash de baseline (heredado de "quien ganó la última acción") contra
            // CUALQUIERA de los dos lados, y como local/remoto calculan su hash sobre campos
            // disjuntos, esa comparación cruzada no tenía punto fijo: cada ciclo alternaba
            // PushUpsert/PullUpsert para siempre aunque nada hubiera cambiado de verdad.
            var localChanged = l is not null && ChangedLocal(b, l);
            var remoteChanged = r is not null && ChangedRemote(b, r);

            if (!localChanged && !remoteChanged)
                continue;

            if (localChanged && !remoteChanged)
            {
                actions.Add(ToAction(l!, push: true, "cambio local"));
            }
            else if (!localChanged && remoteChanged)
            {
                actions.Add(ToAction(r!, push: false, "cambio remoto"));
            }
            else
            {
                // Conflicto: gana el más nuevo (last-write-wins).
                if (l!.UpdatedAt >= r!.UpdatedAt)
                    actions.Add(ToAction(l, push: true, "conflicto: gana local (más nuevo)"));
                else
                    actions.Add(ToAction(r, push: false, "conflicto: gana remoto (más nuevo)"));
            }
        }

        return actions;
    }

    private static SyncItemState? AsLocalFallback(SyncBaselineItem? b) =>
        b is null ? null : new SyncItemState(b.Id, b.Kind, b.LastLocalHash, b.UpdatedAt, b.Deleted);

    private static SyncItemState? AsRemoteFallback(SyncBaselineItem? b) =>
        b is null ? null : new SyncItemState(b.Id, b.Kind, b.LastRemoteHash, b.UpdatedAt, b.Deleted);

    private static bool ChangedLocal(SyncBaselineItem? baseItem, SyncItemState current)
    {
        if (baseItem is null) return true; // nuevo
        return baseItem.Deleted != current.Deleted || baseItem.LastLocalHash != current.ContentHash;
    }

    private static bool ChangedRemote(SyncBaselineItem? baseItem, SyncItemState current)
    {
        if (baseItem is null) return true; // nuevo
        return baseItem.Deleted != current.Deleted || baseItem.LastRemoteHash != current.ContentHash;
    }

    private static SyncAction ToAction(SyncItemState winner, bool push, string reason)
    {
        var type = (push, winner.Deleted) switch
        {
            (true, false) => SyncActionType.PushUpsert,
            (true, true) => SyncActionType.PushDelete,
            (false, false) => SyncActionType.PullUpsert,
            (false, true) => SyncActionType.PullDelete,
        };
        return new SyncAction(winner.Id, winner.Kind, type, reason);
    }
}
