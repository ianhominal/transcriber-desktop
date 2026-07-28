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

    // Conflictos (ADR-07b/I-5: arbitra el SERVIDOR, nunca el reloj del cliente) --------------
    // Bugfix Phase 5 (ADR-07b): antes, un conflicto (cambio local Y remoto) se resolvía por
    // "gana el más nuevo" comparando UpdatedAt -- dos relojes distintos (el mtime del filesystem
    // del usuario contra el updated_at de Postgres) decidiendo una escritura, exactamente lo que
    // prohíbe I-5. Ahora el planner es determinístico y CLOCK-INDEPENDENT: ante conflicto,
    // SIEMPRE push -- el servidor arbitra con base_version (ver SyncEngine.ResolveBaseVersion) y,
    // si el cliente estaba desactualizado, responde "conflict"; ahí el ConflictResolver (Phase 5,
    // ver ConflictResolverTests.cs) preserva ambas copias, sin perder nada.

    [Fact]
    public void ConflictoEdicion_SiempreEmpujaLocal_AunqueElRemotoSeaMasNuevo()
    {
        // Antes de Phase 5 esto ganaba el REMOTO (300 > 200) y generaba un PullUpsert -- ahora el
        // planner ya no mira el reloj: SIEMPRE push, el servidor decide si acepta o rechaza con
        // base_version.
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "vLocal", 200));
        var r = Snap(Item("1", "vRemote", 300));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PushUpsert, a!.Type);
    }

    [Fact]
    public void ConflictoEdicion_SiempreEmpujaLocal_ConElLocalYaMasNuevoTambien()
    {
        var b = BaseSnap(BaselineItem("1", "v1", 100));
        var l = Snap(Item("1", "vLocal", 300));
        var r = Snap(Item("1", "vRemote", 200));
        var a = PlanOne(b, l, r);
        Assert.Equal(SyncActionType.PushUpsert, a!.Type);
    }

    [Fact]
    public void BorradoLocal_VsEdicionRemota_SiempreEmpujaElBorrado_SinImportarCualEsMasNueva()
    {
        // Antes: la edición remota "revivía" el item si era más nueva que el borrado local. Ahora
        // el borrado local SIEMPRE se pushea (push=true, winner=local, Deleted=true -> PushDelete)
        // -- el servidor es quien decide si acepta el borrado o lo rechaza por conflicto de
        // version (y ahí el ConflictResolver preserva la copia remota, no se pierde nada).
        var bRemotoMasNuevo = BaseSnap(BaselineItem("1", "v1", 100));
        var lRemotoMasNuevo = Snap(Item("1", "v1", 150, deleted: true));
        var rRemotoMasNuevo = Snap(Item("1", "vRemote", 300));
        var aRemotoMasNuevo = PlanOne(bRemotoMasNuevo, lRemotoMasNuevo, rRemotoMasNuevo);
        Assert.Equal(SyncActionType.PushDelete, aRemotoMasNuevo!.Type);

        var bLocalMasNuevo = BaseSnap(BaselineItem("1", "v1", 100));
        var lLocalMasNuevo = Snap(Item("1", "v1", 300, deleted: true));
        var rLocalMasNuevo = Snap(Item("1", "vRemote", 200));
        var aLocalMasNuevo = PlanOne(bLocalMasNuevo, lLocalMasNuevo, rLocalMasNuevo);
        Assert.Equal(SyncActionType.PushDelete, aLocalMasNuevo!.Type);
    }

    // ---- Task 5.1 (ADR-07b): arbitraje clock-independiente, explícito -----------------------

    [Fact]
    public void Conflicto_RelojLocalDesincronizado_ProduceElMismoResultadoQueRelojCorrecto()
    {
        // Mismo escenario de conflicto (cambio local Y remoto) corrido dos veces: una con un
        // reloj local "correcto" (más viejo que el remoto, como pasaría de verdad si el usuario
        // editó antes de que llegara el cambio del server) y otra con el reloj local gravemente
        // desincronizado (época Unix, 1970) -- el resultado tiene que ser IDÉNTICO en los dos
        // casos, porque el planner ya no mira ninguno de los dos relojes (I-5).
        var bRelojCorrecto = BaseSnap(BaselineItem("1", "v1", 100));
        var lRelojCorrecto = Snap(Item("1", "vLocal", 150));
        var rRelojCorrecto = Snap(Item("1", "vRemote", 200));
        var accionRelojCorrecto = PlanOne(bRelojCorrecto, lRelojCorrecto, rRelojCorrecto);

        var bRelojRoto = BaseSnap(BaselineItem("1", "v1", 100));
        var lRelojRoto = Snap(Item("1", "vLocal", 0)); // reloj local roto: 1970-01-01
        var rRelojRoto = Snap(Item("1", "vRemote", 200));
        var accionRelojRoto = PlanOne(bRelojRoto, lRelojRoto, rRelojRoto);

        Assert.Equal(SyncActionType.PushUpsert, accionRelojCorrecto!.Type);
        Assert.Equal(accionRelojCorrecto.Type, accionRelojRoto!.Type);
    }
}
