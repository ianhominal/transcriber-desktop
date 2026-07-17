using AudioTranscriber.Core.Runtime;

namespace AudioTranscriber.Core.Tests;

public class StartupWindowModeTests
{
    [Fact]
    public void ShouldStartMinimized_true_when_minimized_flag_present()
    {
        Assert.True(StartupWindowMode.ShouldStartMinimized(new[] { "--minimized" }));
    }

    [Fact]
    public void ShouldStartMinimized_false_when_no_args()
    {
        Assert.False(StartupWindowMode.ShouldStartMinimized(Array.Empty<string>()));
    }

    [Fact]
    public void ShouldStartMinimized_false_for_unrelated_args()
    {
        Assert.False(StartupWindowMode.ShouldStartMinimized(new[] { "--something-else" }));
    }

    [Fact]
    public void ShouldStartMinimized_true_when_flag_present_among_other_args()
    {
        Assert.True(StartupWindowMode.ShouldStartMinimized(new[] { "--foo", "--minimized", "--bar" }));
    }
}
