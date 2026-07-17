using AudioTranscriber.Core.Observability;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncErrorLogFormatterTests
{
    private static readonly DateTime SampleTimestamp = new(2026, 7, 8, 9, 15, 30);

    [Fact]
    public void ResolveLogDirectory_JoinsLocalAppDataWithAppAndLogsFolders()
    {
        var dir = SyncErrorLogFormatter.ResolveLogDirectory(@"C:\Users\ian\AppData\Local");

        Assert.Equal(
            Path.Combine(@"C:\Users\ian\AppData\Local", "AudioTranscriber", "logs"),
            dir);
    }

    [Fact]
    public void ResolveLogFileName_UsesYyyyMMddDatePattern()
    {
        var name = SyncErrorLogFormatter.ResolveLogFileName(new DateTime(2026, 7, 8));

        Assert.Equal("sync-20260708.log", name);
    }

    [Fact]
    public void ResolveLogFilePath_CombinesDirectoryAndFileName()
    {
        var path = SyncErrorLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", SampleTimestamp);

        Assert.Equal(
            Path.Combine(@"C:\LocalAppData", "AudioTranscriber", "logs", "sync-20260708.log"),
            path);
    }

    [Fact]
    public void ResolveLogFilePath_DifferentDates_ProduceDifferentFiles()
    {
        var path1 = SyncErrorLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", new DateTime(2026, 7, 7));
        var path2 = SyncErrorLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", new DateTime(2026, 7, 8));

        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public void FormatEntry_IncluyeTimestampCategoriaTipoYMensaje()
    {
        var ex = new SyncApiException("pull falló (500): Internal Server Error", 500);

        var entry = SyncErrorLogFormatter.FormatEntry(SampleTimestamp, SyncErrorCategory.ServerError, ex);

        Assert.Contains("2026-07-08 09:15:30", entry);
        Assert.Contains("Categoría: ServerError", entry);
        Assert.Contains("SyncApiException", entry);
        Assert.Contains("Internal Server Error", entry);
        Assert.Contains("Stack trace:", entry);
    }

    [Fact]
    public void FormatEntry_SinStackTrace_UsaPlaceholder()
    {
        var ex = new InvalidOperationException("nunca se tiró");

        var entry = SyncErrorLogFormatter.FormatEntry(SampleTimestamp, SyncErrorCategory.Unknown, ex);

        Assert.Contains("(sin stack trace)", entry);
    }

    [Fact]
    public void FormatEntry_TerminaConLineaEnBlancoParaSepararEntradas()
    {
        var ex = new InvalidOperationException("x");

        var entry = SyncErrorLogFormatter.FormatEntry(SampleTimestamp, SyncErrorCategory.Unknown, ex);

        Assert.EndsWith("\n\n", entry);
    }

    [Fact]
    public void FormatEntry_NoLoguearTokens_SoloUsaElMensajeDeLaExcepcion()
    {
        // El detalle de SyncApiException/SyncAuthException viene del cuerpo de la RESPUESTA del
        // backend (motivo del rechazo), nunca del access/refresh token que viajó en el request.
        var ex = new SyncAuthException("Autenticación falló (400): invalid_grant", 400);

        var entry = SyncErrorLogFormatter.FormatEntry(SampleTimestamp, SyncErrorCategory.NeedsLogin, ex);

        Assert.DoesNotContain("Bearer", entry);
        Assert.Contains("invalid_grant", entry);
    }

    [Fact]
    public void FormatEntry_ExcepcionNula_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SyncErrorLogFormatter.FormatEntry(SampleTimestamp, SyncErrorCategory.Unknown, null!));
    }
}
