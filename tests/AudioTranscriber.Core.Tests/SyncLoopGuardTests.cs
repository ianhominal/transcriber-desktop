using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncLoopGuardTests
{
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(3);
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void ShouldIgnoreEvent_SinSyncEnCurso_DevuelveFalse()
    {
        var guard = new SyncLoopGuard(SettleWindow);

        Assert.False(guard.ShouldIgnoreEvent(T0));
    }

    [Fact]
    public void ShouldIgnoreEvent_MientrasElSyncEstaCorriendo_DevuelveTrue()
    {
        var guard = new SyncLoopGuard(SettleWindow);

        guard.BeginSync();

        Assert.True(guard.ShouldIgnoreEvent(T0));
        Assert.True(guard.ShouldIgnoreEvent(T0 + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ShouldIgnoreEvent_DentroDeLaVentanaDeAsentamiento_DevuelveTrue()
    {
        var guard = new SyncLoopGuard(SettleWindow);

        guard.BeginSync();
        guard.EndSync(T0);

        Assert.True(guard.ShouldIgnoreEvent(T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ShouldIgnoreEvent_DespuesDeLaVentanaDeAsentamiento_DevuelveFalse()
    {
        var guard = new SyncLoopGuard(SettleWindow);

        guard.BeginSync();
        guard.EndSync(T0);

        Assert.False(guard.ShouldIgnoreEvent(T0 + SettleWindow));
        Assert.False(guard.ShouldIgnoreEvent(T0 + SettleWindow + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Constructor_ConVentanaNegativa_Lanza()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyncLoopGuard(TimeSpan.FromSeconds(-1)));
    }
}
