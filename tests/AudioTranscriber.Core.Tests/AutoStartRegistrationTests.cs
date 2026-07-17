using AudioTranscriber.Core.Runtime;

namespace AudioTranscriber.Core.Tests;

public class AutoStartRegistrationTests
{
    [Fact]
    public void RunKeyPath_points_to_HKCU_Run()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", AutoStartRegistration.RunKeyPath);
    }

    [Fact]
    public void ValueName_is_AudioTranscriber()
    {
        Assert.Equal("AudioTranscriber", AutoStartRegistration.ValueName);
    }

    [Fact]
    public void MinimizedArg_is_the_expected_flag()
    {
        Assert.Equal("--minimized", AutoStartRegistration.MinimizedArg);
    }

    [Fact]
    public void BuildRegistryValue_quotes_paths_with_spaces_and_appends_minimized_flag()
    {
        var value = AutoStartRegistration.BuildRegistryValue(@"C:\Program Files\AudioTranscriber\AudioTranscriber.exe");

        Assert.Equal("\"C:\\Program Files\\AudioTranscriber\\AudioTranscriber.exe\" --minimized", value);
    }

    [Fact]
    public void BuildRegistryValue_leaves_paths_without_spaces_unquoted_and_appends_minimized_flag()
    {
        var value = AutoStartRegistration.BuildRegistryValue(@"C:\Apps\AudioTranscriber.exe");

        Assert.Equal(@"C:\Apps\AudioTranscriber.exe --minimized", value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildRegistryValue_returns_null_for_missing_path(string? exePath)
    {
        Assert.Null(AutoStartRegistration.BuildRegistryValue(exePath));
    }
}
