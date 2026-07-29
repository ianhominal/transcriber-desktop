using AudioTranscriber.Core.WebImport;

namespace AudioTranscriber.Core.Tests;

public class YtDlpProviderTests : IDisposable
{
    private readonly string _dir;

    public YtDlpProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_ytdlp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ExecutablePath_apunta_a_yt_dlp_exe_dentro_del_directorio()
    {
        var provider = new YtDlpProvider(_dir);

        Assert.Equal(Path.Combine(_dir, "yt-dlp.exe"), provider.ExecutablePath);
    }

    [Fact]
    public void IsAvailable_es_false_sin_el_archivo_y_true_cuando_existe()
    {
        var provider = new YtDlpProvider(_dir);
        Assert.False(provider.IsAvailable);

        File.WriteAllText(provider.ExecutablePath, "fake-binary-bytes");
        Assert.True(provider.IsAvailable);
    }

    [Fact]
    public void EnsureAvailableAsync_no_descarga_nada_si_ya_existe()
    {
        var provider = new YtDlpProvider(_dir);
        File.WriteAllText(provider.ExecutablePath, "ya-esta-aca");

        var path = provider.EnsureAvailableAsync(progress: null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Equal(provider.ExecutablePath, path);
        Assert.Equal("ya-esta-aca", File.ReadAllText(path));
    }
}
