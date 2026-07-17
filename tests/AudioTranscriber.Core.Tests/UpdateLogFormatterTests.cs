using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.Core.Tests;

public class UpdateLogFormatterTests
{
    private static readonly DateTime SampleTimestamp = new(2026, 7, 10, 11, 20, 30);

    [Fact]
    public void ResolveLogDirectory_JoinsLocalAppDataWithAppAndLogsFolders()
    {
        var dir = UpdateLogFormatter.ResolveLogDirectory(@"C:\Users\ian\AppData\Local");

        Assert.Equal(
            Path.Combine(@"C:\Users\ian\AppData\Local", "AudioTranscriber", "logs"),
            dir);
    }

    [Fact]
    public void ResolveLogFileName_UsesYyyyMMddDatePattern()
    {
        var name = UpdateLogFormatter.ResolveLogFileName(new DateTime(2026, 7, 10));

        Assert.Equal("update-20260710.log", name);
    }

    [Fact]
    public void ResolveLogFilePath_CombinesDirectoryAndFileName()
    {
        var path = UpdateLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", SampleTimestamp);

        Assert.Equal(
            Path.Combine(@"C:\LocalAppData", "AudioTranscriber", "logs", "update-20260710.log"),
            path);
    }

    [Fact]
    public void FormatEntry_IncludesTimestampAndMessage()
    {
        var entry = UpdateLogFormatter.FormatEntry(SampleTimestamp, "CheckForUpdatesAsync: available 1.0.24.");

        Assert.Contains("2026-07-10 11:20:30", entry);
        Assert.Contains("CheckForUpdatesAsync: available 1.0.24.", entry);
    }

    [Fact]
    public void FormatEntry_EndsWithNewlineForOneLinePerEntry()
    {
        var entry = UpdateLogFormatter.FormatEntry(SampleTimestamp, "algo");

        Assert.EndsWith("\n", entry);
        Assert.False(entry.TrimEnd('\n').Contains('\n'), "Debe ser una sola línea por entrada.");
    }
}
