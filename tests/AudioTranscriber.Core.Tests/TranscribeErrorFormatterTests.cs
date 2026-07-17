using System;
using System.IO;
using System.Net.Http;
using AudioTranscriber.Core.Transcription;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class TranscribeErrorFormatterTests
{
    /// El caso real reportado con el motor Local: FileNotFoundException del runtime, con la ruta
    /// completa embebida en el Message por defecto.
    private static FileNotFoundException RealLocalFailure() =>
        new("Could not find file 'C:\\Users\\ianho\\AppData\\Local\\AudioTranscriber\\models\\ggml-small.bin'.");

    [Fact]
    public void Local_NeverLeaksTheRawExceptionText()
    {
        var message = TranscribeErrorFormatter.Friendly(RealLocalFailure());

        Assert.DoesNotContain("Could not find file", message);
        Assert.DoesNotContain("C:\\Users", message);
    }

    [Fact]
    public void Local_MissingFile_TellsTheUserToRetry()
    {
        var message = TranscribeErrorFormatter.Friendly(RealLocalFailure());

        Assert.Contains("audio", message);
    }

    [Fact]
    public void CloudTranscriptionException_PassesThroughItsOwnFriendlyMessage()
    {
        // BuildErrorMessage ya arma español amigable (ej. sesión vencida en 401/403): envolverlo de
        // nuevo perdería ese trabajo.
        var ex = new CloudTranscriptionException("Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.");

        var message = TranscribeErrorFormatter.Friendly(ex);

        Assert.Equal(ex.Message, message);
    }

    [Fact]
    public void HttpRequestException_MentionsConnectivity()
    {
        var message = TranscribeErrorFormatter.Friendly(new HttpRequestException("Connection refused"));

        Assert.Contains("conexión", message);
        Assert.DoesNotContain("Connection refused", message);
    }

    [Fact]
    public void UnauthorizedAccessException_SuggestsAnotherFolder()
    {
        var message = TranscribeErrorFormatter.Friendly(new UnauthorizedAccessException("denied"));

        Assert.Contains("permiso", message);
        Assert.Contains("Configuración", message);
    }

    [Fact]
    public void IoException_MentionsSyncAsALikelyCause()
    {
        var message = TranscribeErrorFormatter.Friendly(new IOException("locked"));

        Assert.Contains("sincronizando", message);
    }

    [Fact]
    public void UnknownFailure_FallsBackToAGenericMessage()
    {
        var message = TranscribeErrorFormatter.Friendly(new InvalidOperationException("boom"));

        Assert.Equal("No se pudo transcribir. Probá de nuevo.", message);
    }
}
