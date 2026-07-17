using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncFolderMigrationTests
{
    [Fact]
    public void Resolve_ConCampoNuevoYaSeteado_LoMantiene()
    {
        var result = SyncFolderMigration.Resolve(@"C:\nueva", @"C:\vieja-sync", @"C:\vieja-workspace");

        Assert.Equal(@"C:\nueva", result);
    }

    [Fact]
    public void Resolve_SinCampoNuevo_PrefiereLaCarpetaDeSyncViejaSobreElWorkspace()
    {
        var result = SyncFolderMigration.Resolve(string.Empty, @"C:\vieja-sync", @"C:\vieja-workspace");

        Assert.Equal(@"C:\vieja-sync", result);
    }

    [Fact]
    public void Resolve_SinCampoNuevoNiSyncVieja_CaeAlWorkspaceViejo()
    {
        var result = SyncFolderMigration.Resolve(string.Empty, string.Empty, @"C:\vieja-workspace");

        Assert.Equal(@"C:\vieja-workspace", result);
    }

    [Fact]
    public void Resolve_SinNadaConfigurado_DevuelveVacio()
    {
        var result = SyncFolderMigration.Resolve(null, null, null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Resolve_TrataEspaciosEnBlancoComoNoConfigurado()
    {
        var result = SyncFolderMigration.Resolve("   ", "  ", @"C:\vieja-workspace");

        Assert.Equal(@"C:\vieja-workspace", result);
    }
}
