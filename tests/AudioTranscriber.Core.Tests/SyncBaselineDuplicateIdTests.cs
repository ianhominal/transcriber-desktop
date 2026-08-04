using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Log real de una tester (sync-20260803.log), repetido cada minuto durante todo el día:
///
///   Microsoft.Data.Sqlite.SqliteException
///   SQLite Error 19: 'UNIQUE constraint failed: SyncBaseline.Id'
///   at AudioTranscriber.Core.Sync.SyncIndex.SaveBaseline(...)
///
/// <c>SaveBaseline</c> hace DELETE + INSERT de cada item dentro de UNA transacción, así que el
/// choque de PK aborta el commit entero: la baseline **nunca avanza**. El sync queda roto de forma
/// permanente, no intermitente.
///
/// El origen es <c>ReconcileIdentityRow</c>: al re-identificar un ítem movía la entrada a la clave
/// nueva pero el <see cref="SyncBaselineItem"/> conservaba el <c>Id</c> viejo adentro, así que el
/// diccionario terminaba con dos claves distintas apuntando a items con el MISMO <c>Id</c>.
/// </summary>
public class SyncBaselineDuplicateIdTests
{
    private static SyncBaselineItem Item(string id) =>
        new(id, SyncItemKind.Transcription, "hash-local", "hash-remoto", DateTimeOffset.UnixEpoch);

    [Fact]
    public void MoverUnaEntradaAOtroIdActualizaElIdDeAdentro()
    {
        // Lo que hacía ReconcileIdentityRow: mover el valor sin tocar su Id.
        var baseline = new Dictionary<string, SyncBaselineItem> { ["viejo"] = Item("viejo") };

        Assert.True(baseline.Remove("viejo", out var movida));
        baseline["canonico"] = movida with { Id = "canonico" };

        Assert.Equal("canonico", baseline["canonico"].Id);
        // La clave y el Id de adentro TIENEN que coincidir: SaveBaseline inserta por item.Id, no
        // por la clave, así que una discrepancia acá es una fila con el id equivocado en SQLite.
        foreach (var (clave, item) in baseline)
            Assert.Equal(clave, item.Id);
    }

    [Fact]
    public void DetectaLosIdsDuplicadosDeUnaBaseline()
    {
        var baseline = new Dictionary<string, SyncBaselineItem>
        {
            ["a"] = Item("a"),
            ["b"] = Item("mismo"),
            ["c"] = Item("mismo"), // el choque que rompía el commit entero
        };

        var duplicados = SyncBaselineIntegrity.FindDuplicateIds(baseline);

        Assert.Equal(new[] { "mismo" }, duplicados);
    }

    [Fact]
    public void UnaBaselineSanaNoTieneDuplicados()
    {
        var baseline = new Dictionary<string, SyncBaselineItem> { ["a"] = Item("a"), ["b"] = Item("b") };

        Assert.Empty(SyncBaselineIntegrity.FindDuplicateIds(baseline));
    }

    [Fact]
    public void ToleraUnaBaselineVacia()
    {
        Assert.Empty(SyncBaselineIntegrity.FindDuplicateIds(new Dictionary<string, SyncBaselineItem>()));
    }

    // Perder una entrada duplicada es infinitamente mejor que dejar el sync muerto para siempre:
    // la baseline se reconstruye sola en el próximo ciclo, pero un commit que nunca ocurre no.
    [Fact]
    public void DeduplicarDejaUnaSolaEntradaPorId()
    {
        var baseline = new Dictionary<string, SyncBaselineItem>
        {
            ["a"] = Item("a"),
            ["b"] = Item("mismo"),
            ["c"] = Item("mismo"),
        };

        var limpia = SyncBaselineIntegrity.DeduplicateById(baseline);

        Assert.Equal(2, limpia.Count);
        Assert.Single(limpia.Where(i => i.Id == "mismo"));
        Assert.Single(limpia.Where(i => i.Id == "a"));
    }

    [Fact]
    public void DeduplicarNoTocaUnaBaselineSana()
    {
        var baseline = new Dictionary<string, SyncBaselineItem> { ["a"] = Item("a"), ["b"] = Item("b") };

        var limpia = SyncBaselineIntegrity.DeduplicateById(baseline);

        Assert.Equal(2, limpia.Count);
    }
}
