using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.Core.Tests;

public class StartupLogFormatterTests
{
    private static readonly DateTime SampleTimestamp = new(2026, 7, 8, 9, 15, 0);

    [Fact]
    public void ResolveLogDirectory_JoinsLocalAppDataWithAppAndLogsFolders()
    {
        var dir = StartupLogFormatter.ResolveLogDirectory(@"C:\Users\ian\AppData\Local");

        Assert.Equal(
            Path.Combine(@"C:\Users\ian\AppData\Local", "AudioTranscriber", "logs"),
            dir);
    }

    [Fact]
    public void ResolveLogFileName_UsesYyyyMMddDatePattern()
    {
        var name = StartupLogFormatter.ResolveLogFileName(new DateTime(2026, 7, 8));

        Assert.Equal("startup-20260708.log", name);
    }

    [Fact]
    public void ResolveLogFilePath_CombinesDirectoryAndFileName()
    {
        var path = StartupLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", SampleTimestamp);

        Assert.Equal(
            Path.Combine(@"C:\LocalAppData", "AudioTranscriber", "logs", "startup-20260708.log"),
            path);
    }

    [Fact]
    public void FormatEntry_IncludesTimestampAndMessage()
    {
        var entry = StartupLogFormatter.FormatEntry(SampleTimestamp, "OnStartup begin. Running version: 1.0.17.");

        Assert.Contains("2026-07-08 09:15:00", entry);
        Assert.Contains("OnStartup begin. Running version: 1.0.17.", entry);
    }

    [Fact]
    public void FormatEntry_EndsWithNewlineForOneLinePerEntry()
    {
        var entry = StartupLogFormatter.FormatEntry(SampleTimestamp, "algo");

        Assert.EndsWith("\n", entry);
        Assert.False(entry.TrimEnd('\n').Contains('\n'), "Debe ser una sola línea por entrada.");
    }
}
