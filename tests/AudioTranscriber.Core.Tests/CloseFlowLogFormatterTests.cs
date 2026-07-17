using AudioTranscriber.Core.Observability;
using AudioTranscriber.Core.Runtime;

namespace AudioTranscriber.Core.Tests;

public class CloseFlowLogFormatterTests
{
    private static readonly DateTime SampleTimestamp = new(2026, 7, 8, 9, 15, 0);

    [Fact]
    public void ResolveLogDirectory_JoinsLocalAppDataWithAppAndLogsFolders()
    {
        var dir = CloseFlowLogFormatter.ResolveLogDirectory(@"C:\Users\ian\AppData\Local");

        Assert.Equal(
            Path.Combine(@"C:\Users\ian\AppData\Local", "AudioTranscriber", "logs"),
            dir);
    }

    [Fact]
    public void ResolveLogFileName_UsesYyyyMMddDatePattern()
    {
        var name = CloseFlowLogFormatter.ResolveLogFileName(new DateTime(2026, 7, 8));

        Assert.Equal("close-20260708.log", name);
    }

    [Fact]
    public void ResolveLogFilePath_CombinesDirectoryAndFileName()
    {
        var path = CloseFlowLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", SampleTimestamp);

        Assert.Equal(
            Path.Combine(@"C:\LocalAppData", "AudioTranscriber", "logs", "close-20260708.log"),
            path);
    }

    [Fact]
    public void ResolveLogFilePath_DifferentDates_ProduceDifferentFiles()
    {
        var path1 = CloseFlowLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", new DateTime(2026, 7, 7));
        var path2 = CloseFlowLogFormatter.ResolveLogFilePath(@"C:\LocalAppData", new DateTime(2026, 7, 8));

        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public void FormatEntry_IncludesTimestampSettingExitRequestedAndAction()
    {
        var entry = CloseFlowLogFormatter.FormatEntry(
            SampleTimestamp,
            minimizeToTrayOnClose: true,
            exitRequested: false,
            action: WindowCloseAction.MinimizeToTray);

        Assert.Contains("2026-07-08 09:15:00", entry);
        Assert.Contains("MinimizeToTrayOnClose=True", entry);
        Assert.Contains("exitRequested=False", entry);
        Assert.Contains("action=MinimizeToTray", entry);
    }

    [Fact]
    public void FormatEntry_ExitAction_IsReflectedInEntry()
    {
        var entry = CloseFlowLogFormatter.FormatEntry(
            SampleTimestamp,
            minimizeToTrayOnClose: false,
            exitRequested: true,
            action: WindowCloseAction.Exit);

        Assert.Contains("MinimizeToTrayOnClose=False", entry);
        Assert.Contains("exitRequested=True", entry);
        Assert.Contains("action=Exit", entry);
    }

    [Fact]
    public void FormatEntry_EndsWithNewlineForOneLinePerAttempt()
    {
        var entry = CloseFlowLogFormatter.FormatEntry(
            SampleTimestamp, minimizeToTrayOnClose: true, exitRequested: false, action: WindowCloseAction.MinimizeToTray);

        Assert.EndsWith("\n", entry);
        Assert.False(entry.TrimEnd('\n').Contains('\n'), "Debe ser una sola línea por intento.");
    }
}
