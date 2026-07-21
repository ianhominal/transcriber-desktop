using Microsoft.Data.Sqlite;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Índice local de sync (SQLite), un archivo por carpeta sincronizada. Guarda dos cosas:
/// <list type="bullet">
/// <item>La "baseline": el estado (<see cref="SyncItemState"/>) de cada item tal como quedó
/// al final del último sync exitoso. Es lo que <see cref="SyncPlanner"/> usa para distinguir
/// "no cambió" de "se borró" (ver comentario en SyncPlanner.cs).</item>
/// <item>Un mapa de identidad (path-key -&gt; id) para proyectos/transcripciones que se
/// originaron del lado remoto (bajados por pull): sin esto, <see cref="LocalScanner"/> no
/// tiene forma de saber que una carpeta ya está atada a un id remoto y generaría un id nuevo
/// en cada scan, duplicando el item en el próximo sync.</item>
/// </list>
/// </summary>
public sealed class SyncIndex
{
    private readonly string _connectionString;

    public SyncIndex(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Pooling=False: evita conexiones colgadas que bloqueen el archivo (importante para
        // poder borrar carpetas temporales de test justo después de usar el índice).
        _connectionString = $"Data Source={dbPath};Pooling=False";
        EnsureSchema();
    }

    /// <summary>Ruta por defecto del índice dentro de una carpeta sincronizada.</summary>
    public static string DefaultPathFor(string syncRootPath) =>
        Path.Combine(syncRootPath, ".synccache", "index.db");

    private void EnsureSchema()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS SyncBaseline (
                    Id TEXT PRIMARY KEY,
                    Kind INTEGER NOT NULL,
                    ContentHash TEXT NOT NULL,
                    LastRemoteHash TEXT NOT NULL DEFAULT '',
                    UpdatedAt TEXT NOT NULL,
                    Deleted INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS SyncIdMap (
                    PathKey TEXT PRIMARY KEY,
                    Id TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS SyncLocalTombstone (
                    Id TEXT PRIMARY KEY,
                    Kind INTEGER NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        MigrateAddLastRemoteHashColumnIfMissing(conn);
    }

    /// <summary>
    /// Migración aditiva (bugfix 2026-07-10, ver <see cref="SyncBaselineItem"/>): las bases de
    /// datos creadas ANTES de este fix no tienen la columna <c>LastRemoteHash</c> -- el
    /// <c>CREATE TABLE IF NOT EXISTS</c> de arriba no toca una tabla que ya existe. Se agrega con
    /// <c>ALTER TABLE</c> si falta, con default <c>''</c> (string vacío) para las filas viejas: la
    /// próxima vez que se sincronice ese item, va a verse como "cambiado" en el espacio remoto (una
    /// única reconciliación de más, inofensiva -- no borra nada, ver <see cref="SyncPlanner"/>) y
    /// de ahí en más queda anclado correctamente. La columna <c>ContentHash</c> se mantiene con su
    /// nombre histórico (representa conceptualmente el "LastLocalHash", ver <see cref="LoadBaseline"/>/
    /// <see cref="SaveBaseline"/>) a propósito, para no arriesgar un <c>RENAME COLUMN</c> sobre
    /// datos de usuarios reales.
    /// </summary>
    private static void MigrateAddLastRemoteHashColumnIfMissing(SqliteConnection conn)
    {
        if (HasLastRemoteHashColumn(conn))
            return;

        using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = "ALTER TABLE SyncBaseline ADD COLUMN LastRemoteHash TEXT NOT NULL DEFAULT ''";
        alterCmd.ExecuteNonQuery();
    }

    private static bool HasLastRemoteHashColumn(SqliteConnection conn)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "PRAGMA table_info(SyncBaseline)";
        using var reader = checkCmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "LastRemoteHash", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // ---- Baseline ---------------------------------------------------------

    /// <summary>
    /// Estado de cada item al final del último sync (dos hashes por item, ver
    /// <see cref="SyncBaselineItem"/>). Vacío si nunca se sincronizó.
    /// </summary>
    public Dictionary<string, SyncBaselineItem> LoadBaseline()
    {
        var result = new Dictionary<string, SyncBaselineItem>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Kind, ContentHash, LastRemoteHash, UpdatedAt, Deleted FROM SyncBaseline";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var kind = (SyncItemKind)reader.GetInt32(1);
            var lastLocalHash = reader.GetString(2);
            var lastRemoteHash = reader.GetString(3);
            var updatedAt = DateTimeOffset.Parse(reader.GetString(4));
            var deleted = reader.GetInt32(5) != 0;
            result[id] = new SyncBaselineItem(id, kind, lastLocalHash, lastRemoteHash, updatedAt, deleted);
        }

        return result;
    }

    /// <summary>Reemplaza toda la baseline (semántica replace-all).</summary>
    public void SaveBaseline(IReadOnlyDictionary<string, SyncBaselineItem> baseline)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM SyncBaseline";
            del.ExecuteNonQuery();
        }

        foreach (var item in baseline.Values)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO SyncBaseline (Id, Kind, ContentHash, LastRemoteHash, UpdatedAt, Deleted)
                VALUES ($id, $kind, $hash, $remoteHash, $updatedAt, $deleted)
                """;
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$kind", (int)item.Kind);
            cmd.Parameters.AddWithValue("$hash", item.LastLocalHash);
            cmd.Parameters.AddWithValue("$remoteHash", item.LastRemoteHash);
            cmd.Parameters.AddWithValue("$updatedAt", item.UpdatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$deleted", item.Deleted ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ---- Mapa de identidad (path-key -> id remoto/local) -------------------

    /// <summary>Mapa path-key -&gt; id, para resolver el id de items que vinieron del pull.</summary>
    public Dictionary<string, string> LoadIdMap()
    {
        var result = new Dictionary<string, string>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PathKey, Id FROM SyncIdMap";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetString(1);

        return result;
    }

    /// <summary>Reemplaza todo el mapa de identidad (semántica replace-all).</summary>
    public void SaveIdMap(IReadOnlyDictionary<string, string> pathKeyToId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM SyncIdMap";
            del.ExecuteNonQuery();
        }

        foreach (var (pathKey, id) in pathKeyToId)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO SyncIdMap (PathKey, Id) VALUES ($pathKey, $id)";
            cmd.Parameters.AddWithValue("$pathKey", pathKey);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ---- Tombstones locales (bug #1: un borrado desde el desktop no se propagaba a la nube) ----
    // Señal EXPLÍCITA de que el usuario borró un item (ver Workspace.DeleteAudio + el seam en
    // SyncCoordinator.MarkAudioDeletedForSync) -- a diferencia de la baseline/idMap, esto no es un
    // "replace-all": cada fila se agrega/saca individualmente a medida que el usuario borra y el
    // sync confirma el borrado contra el servidor (ver SyncEngine.RunAsync).

    /// <summary>Tombstones locales pendientes: id -&gt; tipo de item borrado.</summary>
    public Dictionary<string, SyncItemKind> LoadLocalTombstones()
    {
        var result = new Dictionary<string, SyncItemKind>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Kind FROM SyncLocalTombstone";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = (SyncItemKind)reader.GetInt32(1);

        return result;
    }

    /// <summary>Registra (o re-confirma) que el usuario borró explícitamente el item <paramref name="id"/>.</summary>
    public void AddLocalTombstone(string id, SyncItemKind kind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO SyncLocalTombstone (Id, Kind) VALUES ($id, $kind)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", (int)kind);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Saca los tombstones ya resueltos (pusheados con éxito, o stale -- nunca llegaron a estar en
    /// la baseline). Ver <see cref="Sync.SyncEngine.RunAsync"/>: los que quedaron pendientes por un
    /// fallo de red NO se pasan acá, para que se reintenten el próximo ciclo.
    /// </summary>
    public void RemoveLocalTombstones(IEnumerable<string> ids)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        foreach (var id in ids)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM SyncLocalTombstone WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
