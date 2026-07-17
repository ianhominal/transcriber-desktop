using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncWatchFilterTests
{
    private static readonly string Root = Path.Combine("C:", "sync-root");

    [Fact]
    public void ShouldIgnore_ArchivoDentroDeSynccache_DevuelveTrue()
    {
        var path = Path.Combine(Root, ".synccache", "index.db");

        Assert.True(SyncWatchFilter.ShouldIgnore(Root, path));
    }

    [Fact]
    public void ShouldIgnore_ArchivoDentroDePapelera_DevuelveTrue()
    {
        var path = Path.Combine(Root, ".papelera", "20260706_x", "audios", "a.ogg");

        Assert.True(SyncWatchFilter.ShouldIgnore(Root, path));
    }

    [Fact]
    public void ShouldIgnore_ArchivoDeUnProyectoNormal_DevuelveFalse()
    {
        var path = Path.Combine(Root, "audios", "Proyecto1", "nota.ogg");

        Assert.False(SyncWatchFilter.ShouldIgnore(Root, path));
    }

    [Fact]
    public void ShouldIgnore_LaCarpetaRaizMisma_DevuelveFalse()
    {
        Assert.False(SyncWatchFilter.ShouldIgnore(Root, Root));
    }

    [Fact]
    public void ShouldIgnore_CarpetaConNombreParecidoPeroNoIgnorada_DevuelveFalse()
    {
        // ".papelera-vieja" no es ".papelera": no debería matchear por prefijo.
        var path = Path.Combine(Root, ".papelera-vieja", "archivo.txt");

        Assert.False(SyncWatchFilter.ShouldIgnore(Root, path));
    }

    [Fact]
    public void ShouldIgnore_EsInsensibleAMayusculas()
    {
        var path = Path.Combine(Root, ".SYNCCACHE", "index.db");

        Assert.True(SyncWatchFilter.ShouldIgnore(Root, path));
    }
}
