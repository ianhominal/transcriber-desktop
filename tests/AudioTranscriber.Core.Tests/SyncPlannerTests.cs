using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncPlannerTests
{
    private readonly SyncPlanner _planner = new();

    // Helpers -----------------------------------------------------------------
    private static SyncItemState Item(string id, string hash, long unixSeconds, bool deleted = false) =>
        new(id, SyncItemKind.Project, hash, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), deleted);

    private static Dictionary<string, SyncItemState> Snap(params SyncItemState[] items) =>
        items.ToDictionary(i => i.Id);

    // Bugfix 2026-07-10: la baseline ancla DOS hashes por separado (ver SyncBaselineItem). Estos
    // tests, en su mayoría, no necesitan ejercitar la independencia de los dos espacios -- usan el
    // mismo valor de hash en ambos lados ("ya sincronizado"), igual que el modelo viejo de un solo
    // hash. La independencia real la cubre HashesLocalYRemotoDeEspaciosDistintos_... más abajo.
    private static SyncBaselineItem BaselineItem(string id, string hash, long unixSeconds, bool deleted = false) =>
        new(id, SyncItemKind.Project, hash, hash, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), deleted);

    private static Dictionary<string, SyncBaselineItem> BaseSnap(params SyncBaselineItem[] items) =>
        items.ToDictionary(i => i.Id);

    private SyncAction? PlanOne(
        Dictionary<string, SyncBaselineItem> b,
        Dictionary<string, SyncItemState> l,
        Dictionary<string, SyncItemState> r) =>
        _planner.Plan(b, l, r).SingleOrDefault();

    // Sin cambios -------------------------------------------------------------
    [Fact]
    public void SinCambios_NoGeneraAcciones()
    {
        var s = Snap(Item("1", "v1", 100));
        var actions = _planner.Plan(BaseSnap(BaselineItem("1", "v1", 100)), s, Snap(Item("1", "v1", 100)));
        Assert.Empty(actions);
    }

    [Fact]
    public void HashesLocalYRemotoDeEspaciosDistintos_SinCambiosEnNinguno_NoGeneraAcciones()
    {
        // Regresión de raíz del bug de oscilación perpetua (2026-07-10): en producción real,
        // LocalScanner y RemoteMapper SIEMPRE calculan su ContentHash sobre campos DISJUNTOS (ver
        // comentarios en cada uno), así que baseline.LastLocalHash != baseline.LastRemoteHash aunque
        // el ítem esté 100% sincronizado -- eso es NORMAL y esperado, no un bug en sí mismo. Antes
        // del fix, comparar un solo ContentHash cruzado generaba una acción todos los ciclos aunque
        // nada hubiera cambiado. Con el modelo de dos hashes, cada lado se compara SOLO contra su
        // propio espacio -> cero acciones, pese a que los dos hashes de la baseline son distintos
        // entre sí.
        var b = BaseSnap(new SyncBaselineItem(
            "1", SyncItemKind.Transcription, "local-hash-x", "remote-hash-y", DateTimeOffset.FromUnixTimeSeconds(100)));
        var l = Snap(Item("1", "local-hash-x", 100));
        var r = Snap(Item("1", "remote-hash-y", 100));

        var actions = _planner.Plan(b, l, r);

        Assert.Empty(actions);
    }

    // Cambios unilaterales ----------------------------------------------------
    [Fact]
    public void CambioSoloLocal_Push()
    {
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "v2", 200));
        var r = Snap(Item("1", "v1", 100));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PushUpsert, a!.Type);
    }

    [Fact]
    public void CambioSoloRemoto_Pull()
    {
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "v1", 100));
        var r = Snap(Item("1", "v2", 200));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PullUpsert, a!.Type);
    }

    [Fact]
    public void BorradoSoloLocal_PushDelete()
    {
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "v1", 200, deleted: true));
        var r = Snap(Item("1", "v1", 100));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PushDelete, a!.Type);
    }

    [Fact]
    public void BorradoSoloRemoto_PullDelete()
    {
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "v1", 100));
        var r = Snap(Item("1", "v1", 200, deleted: true));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PullDelete, a!.Type);
    }

    // Altas nuevas ------------------------------------------------------------
    [Fact]
    public void NuevoSoloLocal_Push()
    {
        var b = BaseSnap();
        var l = Snap(Item("1", "v1", 100));
        var r = Snap();
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PushUpsert, a!.Type);
    }

    [Fact]
    public void NuevoSoloRemoto_Pull()
    {
        var b = BaseSnap();
        var l = Snap();
        var r = Snap(Item("1", "v1", 100));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PullUpsert, a!.Type);
    }

    // Conflictos (last-write-wins) -------------------------------------------
    [Fact]
    public void ConflictoEdicion_GanaLocalMasNuevo()
    {
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "vLocal", 300));
        var r = Snap(Item("1", "vRemote", 200));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PushUpsert, a!.Type);
    }

    [Fact]
    public void ConflictoEdicion_GanaRemotoMasNuevo()
    {
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "vLocal", 200));
        var r = Snap(Item("1", "vRemote", 300));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PullUpsert, a!.Type);
    }

    [Fact]
    public void BorradoLocal_VsEdicionRemotaMasNueva_Revive()
    {
        // El borrado local es viejo; la edición remota es más nueva → gana la edición.
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "v1", 150, deleted: true));
        var r = Snap(Item("1", "vRemote", 300));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PullUpsert, a!.Type);
    }

    [Fact]
    public void BorradoLocal_VsEdicionRemotaMasVieja_Borra()
    {
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "v1", 300, deleted: true));
        var r = Snap(Item("1", "vRemote", 200));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PushDelete, a!.Type);
    }
}
