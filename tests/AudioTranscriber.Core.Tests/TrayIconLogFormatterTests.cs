using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.Core.Tests;

public class TrayIconLogFormatterTests
{
    private static readonly DateTime SampleTimestamp = new(2026, 7, 8, 9, 15, 0);

    [Fact]
    public void ResolveLogDirectory_JoinsLocalAppDataWithAppAndLogsFolders()
    {
        var dir = TrayIconLogFormatter.ResolveLogDirectory(@"C:\Users\ian\AppData\Local");

        Assert.Equal(
            Path.Combine(@"C:\Users\ian\AppData\Local", "AudioTranscriber", "logs"),
            dir);
    }

    [Fact]
    public void ResolveLogFileName_UsesYyyyMMddDatePattern()
    {
        var name = TrayIconLogFormatter.ResolveLogFileName(new DateTime(2026, 7, 8));

        Assert.Equal("tray-20260708.log", name);
    }

    [Fact]
    public void ResolveLogFilePath_CombinesDirectoryAndFileName()
    {
        var path = TrayIconLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", SampleTimestamp);

        Assert.Equal(
            Path.Combine(@"C:\LocalAppData", "AudioTranscriber", "logs", "tray-20260708.log"),
            path);
    }

    [Fact]
    public void FormatEntry_IncludesTimestampAndMessage()
    {
        var entry = TrayIconLogFormatter.FormatEntry(SampleTimestamp, "AppIcon.ico encontrado, 1234 bytes.");

        Assert.Contains("2026-07-08 09:15:00", entry);
        Assert.Contains("AppIcon.ico encontrado, 1234 bytes.", entry);
    }

    [Fact]
    public void FormatEntry_EndsWithNewlineForOneLinePerEntry()
    {
        var entry = TrayIconLogFormatter.FormatEntry(SampleTimestamp, "algo");

        Assert.EndsWith("\n", entry);
        Assert.False(entry.TrimEnd('\n').Contains('\n'), "Debe ser una sola línea por entrada.");
    }
}
