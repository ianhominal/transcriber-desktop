using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class DebounceCoalescerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void TryConsumeDue_SinSignalPrevio_DevuelveFalse()
    {
        var debouncer = new DebounceCoalescer(Delay);

        Assert.False(debouncer.TryConsumeDue(T0));
        Assert.False(debouncer.HasPending);
    }

    [Fact]
    public void TryConsumeDue_AntesDeVencer_DevuelveFalse()
    {
        var debouncer = new DebounceCoalescer(Delay);

        debouncer.Signal(T0);

        Assert.False(debouncer.TryConsumeDue(T0 + TimeSpan.FromSeconds(1)));
        Assert.True(debouncer.HasPending);
    }

    [Fact]
    public void TryConsumeDue_JustoAlVencer_DevuelveTrueYConsume()
    {
        var debouncer = new DebounceCoalescer(Delay);

        debouncer.Signal(T0);

        Assert.True(debouncer.TryConsumeDue(T0 + Delay));
        Assert.False(debouncer.HasPending);
    }

    [Fact]
    public void Signal_VariasVecesSeguidas_CoalesceEnUnSoloVencimientoDesdeElUltimoEvento()
    {
        // Simula una ráfaga: 3 eventos, cada uno reprograma el vencimiento.
        var debouncer = new DebounceCoalescer(Delay);

        debouncer.Signal(T0);
        debouncer.Signal(T0 + TimeSpan.FromSeconds(1));
        debouncer.Signal(T0 + TimeSpan.FromSeconds(1.5));

        // Todavía no pasaron los 2s desde el ÚLTIMO evento (t=1.5s).
        Assert.False(debouncer.TryConsumeDue(T0 + TimeSpan.FromSeconds(3)));

        // Ahora sí: 2s después del último evento.
        Assert.True(debouncer.TryConsumeDue(T0 + TimeSpan.FromSeconds(3.5)));
    }

    [Fact]
    public void TryConsumeDue_LlamadoDosVecesSeguidas_SoloDisparaUnaVez()
    {
        var debouncer = new DebounceCoalescer(Delay);
        debouncer.Signal(T0);

        Assert.True(debouncer.TryConsumeDue(T0 + Delay));
        Assert.False(debouncer.TryConsumeDue(T0 + Delay + TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Signal_DespuesDeConsumir_ProgramaUnNuevoVencimiento()
    {
        var debouncer = new DebounceCoalescer(Delay);
        debouncer.Signal(T0);
        Assert.True(debouncer.TryConsumeDue(T0 + Delay));

        debouncer.Signal(T0 + Delay);

        Assert.False(debouncer.TryConsumeDue(T0 + Delay + TimeSpan.FromSeconds(1)));
        Assert.True(debouncer.TryConsumeDue(T0 + Delay + Delay));
    }

    [Fact]
    public void Constructor_ConDelayNegativoOCero_Lanza()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DebounceCoalescer(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DebounceCoalescer(TimeSpan.FromSeconds(-1)));
    }
}
