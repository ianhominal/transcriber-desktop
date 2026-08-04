namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Red de seguridad contra ids duplicados en la baseline de sync.
///
/// Caso real (sync-20260803.log de una tester, repetido cada minuto todo el día):
/// <c>SQLite Error 19: 'UNIQUE constraint failed: SyncBaseline.Id'</c> desde
/// <see cref="SyncIndex.SaveBaseline"/>. Ese método hace DELETE + un INSERT por item dentro de UNA
/// transacción, así que el choque de PK aborta el commit COMPLETO: la baseline no avanza nunca y el
/// sync queda roto de forma permanente, no intermitente.
///
/// El origen estaba en <c>ReconcileIdentityRow</c> (ya corregido: el item movido conserva ahora el
/// id nuevo), pero esto queda igual como defensa: cualquier bug futuro que meta dos entradas con el
/// mismo id debe degradar a "se pierde una entrada de baseline" —que el próximo ciclo reconstruye—
/// y no a "el sync no vuelve a funcionar hasta que alguien borre el archivo a mano".
///
/// Lógica pura, sin I/O.
/// </summary>
public static class SyncBaselineIntegrity
{
    /// <summary>Ids que aparecen en más de una entrada. Vacío en una baseline sana.</summary>
    public static IReadOnlyList<string> FindDuplicateIds(IReadOnlyDictionary<string, SyncBaselineItem> baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var vistos = new HashSet<string>(StringComparer.Ordinal);
        var duplicados = new List<string>();
        foreach (var item in baseline.Values)
        {
            if (!vistos.Add(item.Id) && !duplicados.Contains(item.Id, StringComparer.Ordinal))
                duplicados.Add(item.Id);
        }
        return duplicados;
    }

    /// <summary>
    /// Una sola entrada por id, conservando la primera aparición. Lo que entra a SQLite después de
    /// pasar por acá no puede violar el PK.
    /// </summary>
    public static IReadOnlyList<SyncBaselineItem> DeduplicateById(IReadOnlyDictionary<string, SyncBaselineItem> baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var vistos = new HashSet<string>(StringComparer.Ordinal);
        var resultado = new List<SyncBaselineItem>(baseline.Count);
        foreach (var item in baseline.Values)
        {
            if (vistos.Add(item.Id))
                resultado.Add(item);
        }
        return resultado;
    }
}
