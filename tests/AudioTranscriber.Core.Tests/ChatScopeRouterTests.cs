using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class ChatScopeRouterTests
{
    [Fact]
    public void UsesGlobalBrainClient_AlcanceTodasMisNotas_True() =>
        Assert.True(ChatScopeRouter.UsesGlobalBrainClient(ChatScopeRouter.AllNotes));

    [Fact]
    public void UsesGlobalBrainClient_AlcanceEstaNota_False() =>
        Assert.False(ChatScopeRouter.UsesGlobalBrainClient(ChatScopeRouter.ThisNote));

    [Theory]
    [InlineData("")]
    [InlineData("cualquier-otra-cosa")]
    public void UsesGlobalBrainClient_ValorInesperado_CaeEnEstaNota(string scope) =>
        Assert.False(ChatScopeRouter.UsesGlobalBrainClient(scope));
}
