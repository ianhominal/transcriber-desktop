using System.Security.Cryptography;
using System.Text;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Dónde vive el índice de sync (el baseline SQLite del merge de 3 vías).
///
/// Hasta 2026-07-30 vivía DENTRO de la carpeta sincronizada: <c>{workspace}\.synccache\index.db</c>.
/// Para una tester real esa carpeta era <c>C:\Users\Sofia\OneDrive\Documentos\AudioTranscriber</c>,
/// así que el SQLite quedaba adentro de OneDrive. OneDrive lo bloquea mientras sube, lo puede dejar
/// como placeholder sin descargar ("Archivos a pedido") o generar una copia en conflicto; cualquiera
/// de esas rompe el ciclo de sync.
///
/// Y hay un motivo más de fondo: <b>el baseline es POR MÁQUINA</b>. Es el estado del último sync de
/// ESTA computadora. Sincronizarlo hacia otra hace que el merge compare contra un estado ajeno.
/// Una carpeta que se sincroniza sola no es lugar para eso.
///
/// Por eso ahora vive en <c>%LocalAppData%\AudioTranscriber\synccache\{clave}\index.db</c>, con una
/// clave derivada de la ruta del workspace (así dos carpetas distintas no comparten baseline).
/// </summary>
public static class SyncIndexLocation
{
    /// <summary>Carpeta del índice viejo, todavía dentro del workspace (solo para migrar).</summary>
    public const string LegacyFolderName = ".synccache";

    private const string DbFileName = "index.db";
    private const string AppFolderName = "AudioTranscriber";
    private const string CacheFolderName = "synccache";

    /// <summary>Ruta donde vivía el índice antes de la migración.</summary>
    public static string LegacyDbPathFor(string syncRootPath) =>
        Path.Combine(syncRootPath, LegacyFolderName, DbFileName);

    /// <summary>
    /// Clave estable y corta para un workspace. Se normaliza la ruta (sin barra final, en
    /// minúsculas) porque en Windows <c>C:\Trabajo</c> y <c>c:\trabajo\</c> son la MISMA carpeta:
    /// tratarlas distinto dejaría dos baselines separados para el mismo lugar, y el merge compararía
    /// contra un estado incompleto.
    /// </summary>
    public static string WorkspaceKey(string syncRootPath)
    {
        var normalizada = (syncRootPath ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizada));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Ruta definitiva del índice para un workspace. Pura: no toca el disco.</summary>
    public static string ResolveDbPath(string syncRootPath, string localAppDataPath) =>
        Path.Combine(localAppDataPath, AppFolderName, CacheFolderName, WorkspaceKey(syncRootPath), DbFileName);

    /// <summary>
    /// Devuelve la ruta del índice asegurando que la carpeta exista y migrando el índice viejo la
    /// primera vez.
    ///
    /// La migración COPIA (no mueve): si algo sale mal, el índice de siempre sigue en su lugar. Y
    /// nunca tira: perder el baseline degrada a un merge sin base (que reconcilia de más, pero no
    /// borra, porque los borrados viajan por tombstones explícitos), mientras que tirar acá dejaría
    /// el sync muerto.
    /// </summary>
    public static string EnsureLocalDb(string syncRootPath, string localAppDataPath)
    {
        var destino = ResolveDbPath(syncRootPath, localAppDataPath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

            if (!File.Exists(destino))
            {
                var legacy = LegacyDbPathFor(syncRootPath);
                if (File.Exists(legacy))
                    File.Copy(legacy, destino);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Justamente el caso que motivó todo esto: el archivo viejo puede estar tomado por
            // OneDrive o ser un placeholder ilegible. Se sigue con un baseline nuevo.
        }

        return destino;
    }
}
