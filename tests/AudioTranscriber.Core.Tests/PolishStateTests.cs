using AudioTranscriber.Core.Transcription;
using Xunit;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Reglas de "¿ya está mejorado el texto?" (botón "Texto mejorado ✓"). Viven acá, puras, porque son
/// lógica de decisión y no de UI — el ViewModel solo las consume.
/// </summary>
public class PolishStateTests
{
    [Fact]
    public void PulidoCompleto_MarcaElTextoComoMejorado()
    {
        Assert.True(PolishState.ShouldMarkPolished(polishedChunks: 3, totalChunks: 3));
    }

    /// El matiz que importa: un parcial deja el botón habilitado, porque ahí reintentar SÍ sirve.
    [Fact]
    public void PulidoParcial_NO_MarcaComoMejorado()
    {
        Assert.False(PolishState.ShouldMarkPolished(polishedChunks: 2, totalChunks: 3));
    }

    [Fact]
    public void NadaPulidoDeVariosTramos_NO_MarcaComoMejorado()
    {
        Assert.False(PolishState.ShouldMarkPolished(polishedChunks: 0, totalChunks: 3));
    }

    /// Todo el texto era demasiado corto para mejorarlo: no falló nada, no hay nada que reintentar.
    [Fact]
    public void SinNadaQueHacer_MarcaComoMejorado()
    {
        Assert.True(PolishState.ShouldMarkPolished(polishedChunks: 0, totalChunks: 0));
    }

    [Fact]
    public void SinNadaQueHacer_SeDistingueDeUnFallo()
    {
        Assert.True(PolishState.NothingToDo(totalChunks: 0));
        Assert.False(PolishState.NothingToDo(totalChunks: 3));
    }

    [Fact]
    public void EsParcial_SoloCuandoQuedoAlgoSinPulir()
    {
        Assert.True(PolishState.IsPartial(polishedChunks: 1, totalChunks: 3));
        Assert.False(PolishState.IsPartial(polishedChunks: 3, totalChunks: 3));
        // Sin tramos no hay "parcial": no había nada que pulir.
        Assert.False(PolishState.IsPartial(polishedChunks: 0, totalChunks: 0));
    }
}
