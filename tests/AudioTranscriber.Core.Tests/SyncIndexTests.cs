using AudioTranscriber.Core.Sync;
using Microsoft.Data.Sqlite;

namespace AudioTranscriber.Core.Tests;

public class SyncIndexTests : IDisposable
{
    private readonly string _dbPath;

    public SyncIndexTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "at_tests_" + Guid.NewGuid().ToString("N"), "index.db");
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void LoadBaseline_SinDatos_DevuelveVacio()
    {
        var index = new SyncIndex(_dbPath);

        var baseline = index.LoadBaseline();

        Assert.Empty(baseline);
    }

    [Fact]
    public void SaveBaseline_LuegoLoad_DevuelveDatosEquivalentes()
    {
        var index = new SyncIndex(_dbPath);
        var original = new Dictionary<string, SyncBaselineItem>
        {
            ["p1"] = new SyncBaselineItem("p1", SyncItemKind.Project, "hashA", "hashA-remote", DateTimeOffset.FromUnixTimeSeconds(1000)),
            ["t1"] = new SyncBaselineItem("t1", SyncItemKind.Transcription, "hashB", "hashB-remote", DateTimeOffset.FromUnixTimeSeconds(2000), Deleted: true),
        };

        index.SaveBaseline(original);
        var loaded = index.LoadBaseline();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(original["p1"], loaded["p1"]);
        Assert.Equal(original["t1"], loaded["t1"]);
    }

    [Fact]
    public void SaveBaseline_LuegoLoad_PreservaLosDosHashesPorSeparado()
    {
        // Regresión directa del bugfix 2026-07-10: LastLocalHash y LastRemoteHash tienen que
        // sobrevivir el round-trip como valores INDEPENDIENTES (no colapsar a uno solo, no
        // confundirse entre sí).
        var index = new SyncIndex(_dbPath);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            ["t1"] = new SyncBaselineItem("t1", SyncItemKind.Transcription, "local-hash", "remote-hash", DateTimeOffset.FromUnixTimeSeconds(1000)),
        });

        var loaded = index.LoadBaseline();

        Assert.Equal("local-hash", loaded["t1"].LastLocalHash);
        Assert.Equal("remote-hash", loaded["t1"].LastRemoteHash);
    }

    [Fact]
    public void SaveBaseline_EsReplaceAll_NoAcumulaFilasViejas()
    {
        var index = new SyncIndex(_dbPath);
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            ["p1"] = new SyncBaselineItem("p1", SyncItemKind.Project, "hashA", "hashA-remote", DateTimeOffset.FromUnixTimeSeconds(1000)),
        });

        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            ["p2"] = new SyncBaselineItem("p2", SyncItemKind.Project, "hashB", "hashB-remote", DateTimeOffset.FromUnixTimeSeconds(2000)),
        });

        var loaded = index.LoadBaseline();
        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey("p2"));
    }

    [Fact]
    public void EsquemaViejoSinLastRemoteHash_Migra_YLastRemoteHashQuedaVacio()
    {
        // Bugfix 2026-07-10: simula una DB de un usuario real creada ANTES del fix de dos hashes --
        // la tabla SyncBaseline no tenía la columna LastRemoteHash. CREATE TABLE IF NOT EXISTS no
        // toca una tabla que ya existe, así que SyncIndex tiene que migrarla con ALTER TABLE en vez
        // de romper al construirse o al leer/escribir.
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using (var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            conn.Open();
            using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE SyncBaseline (
                    Id TEXT PRIMARY KEY,
                    Kind INTEGER NOT NULL,
                    ContentHash TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    Deleted INTEGER NOT NULL
                );
                """;
            create.ExecuteNonQuery();

            using var insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT INTO SyncBaseline (Id, Kind, ContentHash, UpdatedAt, Deleted) " +
                "VALUES ('p1', 0, 'old-hash', '2026-07-01T00:00:00+00:00', 0)";
            insert.ExecuteNonQuery();
        }

        // Construir SyncIndex sobre esta DB "vieja" no debe tirar, y debe migrar la columna.
        var index = new SyncIndex(_dbPath);
        var baseline = index.LoadBaseline();

        var entry = Assert.Single(baseline.Values);
        Assert.Equal("old-hash", entry.LastLocalHash);
        Assert.Equal(string.Empty, entry.LastRemoteHash);
        Assert.False(entry.Deleted);

        // Y sigue siendo usable para guardar de ahí en más (no quedó en un estado a medio migrar).
        index.SaveBaseline(new Dictionary<string, SyncBaselineItem>
        {
            ["p1"] = entry with { LastRemoteHash = "remote-now" },
        });
        Assert.Equal("remote-now", index.LoadBaseline()["p1"].LastRemoteHash);
    }

    [Fact]
    public void LoadIdMap_SinDatos_DevuelveVacio()
    {
        var index = new SyncIndex(_dbPath);

        Assert.Empty(index.LoadIdMap());
    }

    [Fact]
    public void SaveIdMap_LuegoLoad_RoundTrip()
    {
        var index = new SyncIndex(_dbPath);
        var map = new Dictionary<string, string> { ["project:Trabajo"] = "remote-id-1" };

        index.SaveIdMap(map);
        var loaded = index.LoadIdMap();

        Assert.Equal("remote-id-1", loaded["project:Trabajo"]);
    }

    // ---- Tombstones locales (bug #1: un borrado desde el desktop no se propagaba a la nube) ----
    // Señal explícita del usuario ("Borrar") que SyncEngine.MergeWithLocalTombstones usa para
    // inyectar Deleted=true SOLO para items que el usuario borró de verdad -- ver el freno de
    // seguridad en SyncEngineTests (la ausencia SOLA, sin este tombstone, nunca genera un borrado).

    [Fact]
    public void LoadLocalTombstones_SinDatos_DevuelveVacio()
    {
        var index = new SyncIndex(_dbPath);

        Assert.Empty(index.LoadLocalTombstones());
    }

    [Fact]
    public void AddLocalTombstone_LuegoLoad_RoundTrip()
    {
        var index = new SyncIndex(_dbPath);

        index.AddLocalTombstone("t1", SyncItemKind.Transcription);
        var loaded = index.LoadLocalTombstones();

        Assert.Single(loaded);
        Assert.Equal(SyncItemKind.Transcription, loaded["t1"]);
    }

    [Fact]
    public void AddLocalTombstone_MismoId_ReemplazaSinDuplicar()
    {
        var index = new SyncIndex(_dbPath);

        index.AddLocalTombstone("t1", SyncItemKind.Transcription);
        index.AddLocalTombstone("t1", SyncItemKind.Transcription);
        var loaded = index.LoadLocalTombstones();

        Assert.Single(loaded);
    }

    [Fact]
    public void RemoveLocalTombstones_SacaSoloLosIndicados()
    {
        var index = new SyncIndex(_dbPath);
        index.AddLocalTombstone("t1", SyncItemKind.Transcription);
        index.AddLocalTombstone("t2", SyncItemKind.Transcription);

        index.RemoveLocalTombstones(new[] { "t1" });
        var loaded = index.LoadLocalTombstones();

        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey("t2"));
    }
}
