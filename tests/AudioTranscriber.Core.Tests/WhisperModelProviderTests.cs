using AudioTranscriber.Core.Transcription;
using Whisper.net.Ggml;

namespace AudioTranscriber.Core.Tests;

public class WhisperModelProviderTests : IDisposable
{
    private readonly string _dir;

    public WhisperModelProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_model_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ModelPath_is_a_bin_file_inside_the_model_dir()
    {
        var provider = new WhisperModelProvider(_dir, GgmlType.Small, QuantizationType.Q5_0);

        Assert.StartsWith(_dir, provider.ModelPath);
        Assert.EndsWith(".bin", provider.ModelPath);
    }

    [Fact]
    public void IsModelAvailable_is_false_when_file_missing_and_true_when_present()
    {
        var provider = new WhisperModelProvider(_dir, GgmlType.Small, QuantizationType.Q5_0);
        Assert.False(provider.IsModelAvailable);

        File.WriteAllText(provider.ModelPath, "fake-model-bytes");
        Assert.True(provider.IsModelAvailable);
    }

    [Fact]
    public void EnsureModelAsync_skips_download_when_model_already_exists()
    {
        var provider = new WhisperModelProvider(_dir, GgmlType.Small, QuantizationType.Q5_0);
        File.WriteAllText(provider.ModelPath, "already-here");

        // No debe intentar descargar (no red): devuelve la ruta existente sin lanzar.
        var path = provider.EnsureModelAsync(progress: null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(provider.ModelPath, path);
        Assert.Equal("already-here", File.ReadAllText(path));
    }
}
