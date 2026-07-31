using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Una tester real vio el chip "Error de sincronización" a secas. Ese texto es el caso
/// <see cref="SyncErrorCategory.Unknown"/>: el clasificador solo cubría sesión, red y 5xx, así que
/// cualquier problema de disco (índice bloqueado por OneDrive, carpeta offline, sin permisos) caía
/// en el cajón de "algo pasó" y no le decía a la persona qué hacer.
/// </summary>
public class SyncErrorClassifierStorageTests
{
    [Fact]
    public void UnArchivoBloqueadoSeClasificaComoProblemaDeAlmacenamiento()
    {
        var ex = new IOException("The process cannot access the file because it is being used by another process.");

        Assert.Equal(SyncErrorCategory.StorageError, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void LaCarpetaQueDesaparecioSeClasificaComoProblemaDeAlmacenamiento()
    {
        Assert.Equal(SyncErrorCategory.StorageError, SyncErrorClassifier.Classify(new DirectoryNotFoundException()));
        Assert.Equal(SyncErrorCategory.StorageError, SyncErrorClassifier.Classify(new FileNotFoundException()));
    }

    [Fact]
    public void SinPermisosSeClasificaComoProblemaDeAlmacenamiento()
    {
        Assert.Equal(SyncErrorCategory.StorageError, SyncErrorClassifier.Classify(new UnauthorizedAccessException()));
    }

    [Fact]
    public void ElMensajeDeAlmacenamientoHablaDeLaCarpetaYNoDeUnErrorGenerico()
    {
        var chip = SyncErrorMessages.ChipFor(SyncErrorCategory.StorageError);
        var detalle = SyncErrorMessages.StatusMessageFor(SyncErrorCategory.StorageError, "da igual");

        Assert.NotEqual("Error de sincronización", chip);
        Assert.Contains("carpeta", detalle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnFalloDeRedSigueSiendoDeRed()
    {
        // HttpRequestException hereda de Exception, no de IOException: el orden de las reglas no
        // puede robarle los errores de red a su categoría.
        Assert.Equal(SyncErrorCategory.NetworkError, SyncErrorClassifier.Classify(new HttpRequestException("sin dns")));
        Assert.Equal(SyncErrorCategory.NetworkError, SyncErrorClassifier.Classify(new TaskCanceledException()));
    }

    [Fact]
    public void UnErrorDelServidorSigueSiendoDelServidor()
    {
        Assert.Equal(SyncErrorCategory.ServerError, SyncErrorClassifier.Classify(new SyncApiException("boom", 500)));
    }

    [Fact]
    public void UnErrorNoContempladoSigueCayendoEnUnknown()
    {
        Assert.Equal(SyncErrorCategory.Unknown, SyncErrorClassifier.Classify(new InvalidOperationException("raro")));
    }
}
