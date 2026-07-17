using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class GoogleDriveLinkBuilderTests
{
    [Fact]
    public void BuildSettingsUrl_ConBaseSinBarraFinal_AgregaPathDeAjustes()
    {
        var url = GoogleDriveLinkBuilder.BuildSettingsUrl("https://audio-transcriber-web-kappa.vercel.app");

        Assert.Equal("https://audio-transcriber-web-kappa.vercel.app/app/ajustes", url);
    }

    [Fact]
    public void BuildSettingsUrl_ConBarraFinal_NoDuplicaLaBarra()
    {
        var url = GoogleDriveLinkBuilder.BuildSettingsUrl("https://audio-transcriber-web-kappa.vercel.app/");

        Assert.Equal("https://audio-transcriber-web-kappa.vercel.app/app/ajustes", url);
    }

    [Fact]
    public void BuildSettingsUrl_UsaLaConstanteDeSyncConfig_ComoEnLaAppReal()
    {
        var url = GoogleDriveLinkBuilder.BuildSettingsUrl(SyncConfig.BackendBaseUrl);

        Assert.Equal("https://audio-transcriber-web-kappa.vercel.app/app/ajustes", url);
    }
}
