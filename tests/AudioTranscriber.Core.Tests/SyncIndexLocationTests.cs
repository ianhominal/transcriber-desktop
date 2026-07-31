using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// El índice de sync (baseline del merge de 3 vías) vivía DENTRO de la carpeta sincronizada, en
/// <c>{workspace}\.synccache\index.db</c>. Para una tester real esa carpeta era
/// <c>C:\Users\Sofia\OneDrive\Documentos\AudioTranscriber</c>: el SQLite quedaba adentro de OneDrive,
/// que lo bloquea mientras sincroniza, lo puede dejar como placeholder sin descargar, o generar una
/// copia en conflicto. Cualquiera de esas rompe el sync con un error que el clasificador no
/// contemplaba ("Error de sincronización" a secas).
///
/// Peor todavía: el baseline es POR MÁQUINA. Sincronizarlo entre máquinas hace que el merge compare
/// contra el estado de otra computadora.
/// </summary>
public class SyncIndexLocationTests
{
    private const string LocalAppData = @"C:\Users\Tester\AppData\Local";

    [Fact]
    public void ElIndiceQuedaFueraDeLaCarpetaSincronizada()
    {
        var ruta = SyncIndexLocation.ResolveDbPath(@"C:\Users\Sofia\OneDrive\Documentos\AudioTranscriber", LocalAppData);

        Assert.StartsWith(LocalAppData, ruta, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OneDrive", ruta, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DosWorkspacesDistintosNoCompartenIndice()
    {
        var a = SyncIndexLocation.ResolveDbPath(@"C:\Trabajo\Audios", LocalAppData);
        var b = SyncIndexLocation.ResolveDbPath(@"C:\Personal\Audios", LocalAppData);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ElMismoWorkspaceSiempreResuelveALaMismaRuta()
    {
        var a = SyncIndexLocation.ResolveDbPath(@"C:\Trabajo\Audios", LocalAppData);
        var b = SyncIndexLocation.ResolveDbPath(@"C:\Trabajo\Audios", LocalAppData);

        Assert.Equal(a, b);
    }

    [Fact]
    public void IgnoraDiferenciasDeMayusculasYBarraFinal()
    {
        // Windows no distingue mayúsculas en rutas: la misma carpeta escrita distinto no puede
        // terminar con dos baselines separadas (sería un merge contra un estado incompleto).
        var a = SyncIndexLocation.ResolveDbPath(@"C:\Trabajo\Audios", LocalAppData);
        var b = SyncIndexLocation.ResolveDbPath(@"c:\trabajo\audios\", LocalAppData);

        Assert.Equal(a, b);
    }

    [Fact]
    public void LaRutaLegacySigueApuntandoDentroDelWorkspace()
    {
        var legacy = SyncIndexLocation.LegacyDbPathFor(@"C:\Trabajo\Audios");

        Assert.Equal(Path.Combine(@"C:\Trabajo\Audios", ".synccache", "index.db"), legacy);
    }

    [Fact]
    public void MigraElIndiceViejoLaPrimeraVez()
    {
        using var temp = new CarpetaTemporal();
        var workspace = temp.Crear("workspace");
        var localAppData = temp.Crear("localappdata");

        var legacy = SyncIndexLocation.LegacyDbPathFor(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "baseline-viejo");

        var ruta = SyncIndexLocation.EnsureLocalDb(workspace, localAppData);

        Assert.True(File.Exists(ruta));
        Assert.Equal("baseline-viejo", File.ReadAllText(ruta));
        // El viejo NO se borra: si algo sale mal con la migración, sigue estando.
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void NoPisaUnIndiceNuevoYaExistente()
    {
        using var temp = new CarpetaTemporal();
        var workspace = temp.Crear("workspace");
        var localAppData = temp.Crear("localappdata");

        var destino = SyncIndexLocation.ResolveDbPath(workspace, localAppData);
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        File.WriteAllText(destino, "baseline-actual");

        var legacy = SyncIndexLocation.LegacyDbPathFor(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "baseline-viejo");

        SyncIndexLocation.EnsureLocalDb(workspace, localAppData);

        Assert.Equal("baseline-actual", File.ReadAllText(destino));
    }

    [Fact]
    public void SinIndiceViejoSimplementeDevuelveLaRutaNueva()
    {
        using var temp = new CarpetaTemporal();
        var workspace = temp.Crear("workspace");
        var localAppData = temp.Crear("localappdata");

        var ruta = SyncIndexLocation.EnsureLocalDb(workspace, localAppData);

        Assert.Equal(SyncIndexLocation.ResolveDbPath(workspace, localAppData), ruta);
        Assert.True(Directory.Exists(Path.GetDirectoryName(ruta)));
    }

    [Fact]
    public void UnaMigracionQueFallaNoTiraLaExcepcionHaciaArriba()
    {
        using var temp = new CarpetaTemporal();
        var workspace = temp.Crear("workspace");
        var localAppData = temp.Crear("localappdata");

        // El "índice viejo" es una CARPETA, no un archivo: copiarlo falla. El sync tiene que poder
        // seguir con un baseline nuevo en vez de morirse.
        var legacy = SyncIndexLocation.LegacyDbPathFor(workspace);
        Directory.CreateDirectory(legacy);

        var ruta = SyncIndexLocation.EnsureLocalDb(workspace, localAppData);

        Assert.Equal(SyncIndexLocation.ResolveDbPath(workspace, localAppData), ruta);
    }

    private sealed class CarpetaTemporal : IDisposable
    {
        private readonly string _raiz = Path.Combine(Path.GetTempPath(), "at_test_" + Guid.NewGuid().ToString("N"));

        public string Crear(string nombre)
        {
            var ruta = Path.Combine(_raiz, nombre);
            Directory.CreateDirectory(ruta);
            return ruta;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_raiz))
                    Directory.Delete(_raiz, recursive: true);
            }
            catch
            {
                // Limpieza best-effort: un archivo tomado por el antivirus no puede voltear el test.
            }
        }
    }
}
