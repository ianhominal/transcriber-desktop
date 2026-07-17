using AudioTranscriber.Core.Runtime;

namespace AudioTranscriber.Core.Tests;

public class WindowCloseBehaviorTests
{
    [Fact]
    public void Resolve_minimizes_to_tray_when_setting_on_and_not_exiting()
    {
        var action = WindowCloseBehavior.Resolve(minimizeToTrayOnClose: true, exitRequested: false);

        Assert.Equal(WindowCloseAction.MinimizeToTray, action);
    }

    [Fact]
    public void Resolve_exits_when_setting_off_and_not_exiting()
    {
        var action = WindowCloseBehavior.Resolve(minimizeToTrayOnClose: false, exitRequested: false);

        Assert.Equal(WindowCloseAction.Exit, action);
    }

    [Fact]
    public void Resolve_always_exits_when_exit_was_explicitly_requested_even_if_minimize_setting_is_on()
    {
        var action = WindowCloseBehavior.Resolve(minimizeToTrayOnClose: true, exitRequested: true);

        Assert.Equal(WindowCloseAction.Exit, action);
    }

    [Fact]
    public void Resolve_exits_when_exit_was_explicitly_requested_and_minimize_setting_is_off()
    {
        var action = WindowCloseBehavior.Resolve(minimizeToTrayOnClose: false, exitRequested: true);

        Assert.Equal(WindowCloseAction.Exit, action);
    }
}
