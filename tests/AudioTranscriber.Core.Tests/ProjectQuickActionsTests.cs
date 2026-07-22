using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class ProjectQuickActionsTests
{
    [Fact]
    public void Summarize_TextoExactoDeParidadConLaWeb() =>
        Assert.Equal("Resumí este proyecto en los puntos clave.", ProjectQuickActions.Summarize);

    [Fact]
    public void NextSteps_TextoExactoDeParidadConLaWeb() =>
        Assert.Equal("¿Cuáles son los próximos pasos o pendientes según estas notas?", ProjectQuickActions.NextSteps);
}
