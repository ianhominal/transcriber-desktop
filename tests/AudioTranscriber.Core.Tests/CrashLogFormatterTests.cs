using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.Core.Tests;

public class CrashLogFormatterTests
{
    private static readonly DateTime SampleTimestamp = new(2026, 7, 7, 14, 30, 5);

    [Fact]
    public void ResolveLogDirectory_JoinsLocalAppDataWithAppAndLogsFolders()
    {
        var dir = CrashLogFormatter.ResolveLogDirectory(@"C:\Users\ian\AppData\Local");

        Assert.Equal(
            Path.Combine(@"C:\Users\ian\AppData\Local", "AudioTranscriber", "logs"),
            dir);
    }

    [Fact]
    public void ResolveLogFileName_UsesYyyyMMddDatePattern()
    {
        var name = CrashLogFormatter.ResolveLogFileName(new DateTime(2026, 7, 7));

        Assert.Equal("crash-20260707.log", name);
    }

    [Fact]
    public void ResolveLogFilePath_CombinesDirectoryAndFileName()
    {
        var path = CrashLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", SampleTimestamp);

        Assert.Equal(
            Path.Combine(@"C:\LocalAppData", "AudioTranscriber", "logs", "crash-20260707.log"),
            path);
    }

    [Fact]
    public void ResolveLogFilePath_DifferentDates_ProduceDifferentFiles()
    {
        var path1 = CrashLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", new DateTime(2026, 7, 7));
        var path2 = CrashLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", new DateTime(2026, 7, 8));

        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public void FormatEntry_IncludesTimestampVersionTypeMessageAndStackTrace()
    {
        Exception ex;
        try { throw new InvalidOperationException("algo salió mal"); }
        catch (InvalidOperationException caught) { ex = caught; }

        var entry = CrashLogFormatter.FormatEntry(SampleTimestamp, "1.0.6", ex);

        Assert.Contains("2026-07-07 14:30:05", entry);
        Assert.Contains("Versión: 1.0.6", entry);
        Assert.Contains("System.InvalidOperationException", entry);
        Assert.Contains("algo salió mal", entry);
        Assert.Contains("Stack trace:", entry);
        Assert.Contains(nameof(FormatEntry_IncludesTimestampVersionTypeMessageAndStackTrace), entry);
    }

    [Fact]
    public void FormatEntry_WithInnerException_IncludesBothExceptionsInOrder()
    {
        var inner = new ArgumentException("valor inválido");
        var outer = new InvalidOperationException("fallo la operación", inner);

        var entry = CrashLogFormatter.FormatEntry(SampleTimestamp, "1.0.6", outer);

        Assert.Contains("System.InvalidOperationException", entry);
        Assert.Contains("fallo la operación", entry);
        Assert.Contains("System.ArgumentException", entry);
        Assert.Contains("valor inválido", entry);

        var outerIndex = entry.IndexOf("fallo la operación", StringComparison.Ordinal);
        var innerIndex = entry.IndexOf("valor inválido", StringComparison.Ordinal);
        Assert.True(outerIndex < innerIndex, "La excepción externa debe aparecer antes que la interna.");
    }

    [Fact]
    public void FormatEntry_WithoutStackTrace_UsesPlaceholder()
    {
        var ex = new InvalidOperationException("nunca se tiró");

        var entry = CrashLogFormatter.FormatEntry(SampleTimestamp, "1.0.6", ex);

        Assert.Contains("(sin stack trace)", entry);
    }

    [Fact]
    public void FormatEntry_EndsWithBlankLineToSeparateConsecutiveEntries()
    {
        var ex = new InvalidOperationException("x");

        var entry = CrashLogFormatter.FormatEntry(SampleTimestamp, "1.0.6", ex);

        Assert.EndsWith("\n\n", entry);
    }

    [Fact]
    public void FormatEntry_NullException_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CrashLogFormatter.FormatEntry(SampleTimestamp, "1.0.6", null!));
    }
}
