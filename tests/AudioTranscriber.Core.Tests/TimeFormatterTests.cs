using AudioTranscriber.Core.Common;

namespace AudioTranscriber.Core.Tests;

public class TimeFormatterTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(5, "00:05")]
    [InlineData(65, "01:05")]
    [InlineData(599, "09:59")]
    [InlineData(3599, "59:59")]
    public void Format_MenosDeUnaHora_UsaMmSs(int totalSeconds, string expected)
    {
        var result = TimeFormatter.Format(TimeSpan.FromSeconds(totalSeconds));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(7325, "2:02:05")]
    public void Format_UnaHoraOMas_UsaHMmSs(int totalSeconds, string expected)
    {
        var result = TimeFormatter.Format(TimeSpan.FromSeconds(totalSeconds));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Format_Negativo_SeTrataComoCero()
    {
        var result = TimeFormatter.Format(TimeSpan.FromSeconds(-5));
        Assert.Equal("00:00", result);
    }
}
