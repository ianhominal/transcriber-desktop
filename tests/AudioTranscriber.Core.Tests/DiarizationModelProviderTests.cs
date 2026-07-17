using AudioTranscriber.Core.Diarization;

namespace AudioTranscriber.Core.Tests;

public class DiarizationModelProviderTests : IDisposable
{
    private readonly string _dir;

    public DiarizationModelProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_diarization_model_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Model_paths_are_files_inside_the_model_dir_and_different_from_each_other()
    {
        var provider = new DiarizationModelProvider(_dir);

        Assert.StartsWith(_dir, provider.SegmentationModelPath);
        Assert.StartsWith(_dir, provider.EmbeddingModelPath);
        Assert.NotEqual(provider.SegmentationModelPath, provider.EmbeddingModelPath);
    }

    [Fact]
    public void IsModelAvailable_is_false_when_both_files_are_missing()
    {
        var provider = new DiarizationModelProvider(_dir);

        Assert.False(provider.IsSegmentationModelAvailable);
        Assert.False(provider.IsEmbeddingModelAvailable);
        Assert.False(provider.IsModelAvailable);
    }

    [Fact]
    public void IsModelAvailable_is_false_when_only_one_of_the_two_files_is_present()
    {
        var provider = new DiarizationModelProvider(_dir);
        File.WriteAllText(provider.SegmentationModelPath, "fake-segmentation-bytes");

        // Con un solo modelo de los dos no alcanza: identificar hablantes necesita AMBOS.
        Assert.True(provider.IsSegmentationModelAvailable);
        Assert.False(provider.IsEmbeddingModelAvailable);
        Assert.False(provider.IsModelAvailable);
    }

    [Fact]
    public void IsModelAvailable_is_true_only_when_both_files_are_present()
    {
        var provider = new DiarizationModelProvider(_dir);
        File.WriteAllText(provider.SegmentationModelPath, "fake-segmentation-bytes");
        File.WriteAllText(provider.EmbeddingModelPath, "fake-embedding-bytes");

        Assert.True(provider.IsSegmentationModelAvailable);
        Assert.True(provider.IsEmbeddingModelAvailable);
        Assert.True(provider.IsModelAvailable);
    }

    [Fact]
    public async Task EnsureModelAsync_skips_download_when_both_models_already_exist()
    {
        var provider = new DiarizationModelProvider(_dir);
        File.WriteAllText(provider.SegmentationModelPath, "already-here-segmentation");
        File.WriteAllText(provider.EmbeddingModelPath, "already-here-embedding");

        // No debe intentar descargar nada (no red): si lo intentara, esto tardaría o fallaría por
        // falta de conexión en el entorno de tests.
        await provider.EnsureModelAsync(progress: null, CancellationToken.None);

        Assert.Equal("already-here-segmentation", File.ReadAllText(provider.SegmentationModelPath));
        Assert.Equal("already-here-embedding", File.ReadAllText(provider.EmbeddingModelPath));
    }
}
