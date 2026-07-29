using AudioTranscriber.Core.WebImport;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// <see cref="WebPageAnalyzer"/> con <see cref="FakeYtDlpProcessRunner"/> en vez del proceso real
/// -- cero red, cero yt-dlp real. Mismo patrón que <c>CloudTranscriptionServiceTests.FakeHandler</c>
/// para HttpClient.
/// </summary>
public class WebPageAnalyzerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _fakeExePath;

    public WebPageAnalyzerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_analyzer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _fakeExePath = Path.Combine(_dir, "yt-dlp.exe");
        File.WriteAllText(_fakeExePath, "fake");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeYtDlpProcessRunner : IYtDlpProcessRunner
    {
        public YtDlpProcessResult Result { get; set; } = new(0, "{}", string.Empty);
        public IReadOnlyList<string>? LastArguments { get; private set; }

        public Task<YtDlpProcessResult> RunAsync(
            string executablePath, IReadOnlyList<string> arguments, IProgress<string>? onOutputLine, CancellationToken ct)
        {
            LastArguments = arguments;
            return Task.FromResult(Result);
        }
    }

    [Theory]
    [InlineData("no-es-una-url")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://algo.com/x")]
    public async Task Url_invalida_devuelve_InvalidUrl_sin_correr_yt_dlp(string url)
    {
        var runner = new FakeYtDlpProcessRunner();
        var analyzer = new WebPageAnalyzer(runner, _fakeExePath);

        var result = await analyzer.AnalyzeAsync(url);

        Assert.Equal(WebImportStatus.InvalidUrl, result.Status);
        Assert.Null(result.Analysis);
        Assert.Null(runner.LastArguments);
    }

    [Fact]
    public async Task Url_null_devuelve_InvalidUrl()
    {
        var analyzer = new WebPageAnalyzer(new FakeYtDlpProcessRunner(), _fakeExePath);

        var result = await analyzer.AnalyzeAsync(null);

        Assert.Equal(WebImportStatus.InvalidUrl, result.Status);
    }

    [Fact]
    public async Task Sin_yt_dlp_en_disco_devuelve_YtDlpNotAvailable()
    {
        var missingPath = Path.Combine(_dir, "no-existe.exe");
        var analyzer = new WebPageAnalyzer(new FakeYtDlpProcessRunner(), missingPath);

        var result = await analyzer.AnalyzeAsync("https://example.com/video");

        Assert.Equal(WebImportStatus.YtDlpNotAvailable, result.Status);
        Assert.Contains("yt-dlp", result.ErrorMessage);
    }

    [Fact]
    public async Task Corre_yt_dlp_con_dump_single_json_y_flat_playlist()
    {
        var runner = new FakeYtDlpProcessRunner
        {
            Result = new YtDlpProcessResult(0, """{ "id": "x", "title": "T" }""", string.Empty),
        };
        var analyzer = new WebPageAnalyzer(runner, _fakeExePath);

        await analyzer.AnalyzeAsync("https://example.com/video");

        Assert.NotNull(runner.LastArguments);
        Assert.Contains("--dump-single-json", runner.LastArguments!);
        Assert.Contains("--flat-playlist", runner.LastArguments!);
        Assert.Contains("https://example.com/video", runner.LastArguments!);
    }

    [Fact]
    public async Task Exit_code_exitoso_con_json_valido_devuelve_Success()
    {
        var runner = new FakeYtDlpProcessRunner
        {
            Result = new YtDlpProcessResult(0, """{ "id": "x", "title": "Un video" }""", string.Empty),
        };
        var analyzer = new WebPageAnalyzer(runner, _fakeExePath);

        var result = await analyzer.AnalyzeAsync("https://example.com/video");

        Assert.Equal(WebImportStatus.Success, result.Status);
        Assert.NotNull(result.Analysis);
        Assert.Equal("Un video", result.Analysis!.Items[0].Title);
    }

    [Fact]
    public async Task Exit_code_distinto_de_cero_se_clasifica_via_YtDlpErrorClassifier()
    {
        var runner = new FakeYtDlpProcessRunner
        {
            Result = new YtDlpProcessResult(1, string.Empty, "ERROR: Private video. Sign in if you've been granted access"),
        };
        var analyzer = new WebPageAnalyzer(runner, _fakeExePath);

        var result = await analyzer.AnalyzeAsync("https://example.com/video");

        Assert.Equal(WebImportStatus.RequiresLogin, result.Status);
        Assert.Equal("El video es privado o requiere iniciar sesión para verlo.", result.ErrorMessage);
    }

    [Fact]
    public async Task Json_de_salida_invalido_devuelve_el_estado_del_YtDlpParseException()
    {
        var runner = new FakeYtDlpProcessRunner
        {
            Result = new YtDlpProcessResult(0, "{}", string.Empty),
        };
        var analyzer = new WebPageAnalyzer(runner, _fakeExePath);

        var result = await analyzer.AnalyzeAsync("https://example.com/video");

        Assert.Equal(WebImportStatus.NoAudioFound, result.Status);
        Assert.Equal("No se encontró audio en esa página.", result.ErrorMessage);
    }

    [Fact]
    public async Task Cancelacion_se_propaga_sin_convertirse_en_estado_de_error()
    {
        var runner = new ThrowingRunner();
        var analyzer = new WebPageAnalyzer(runner, _fakeExePath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException (deriva de OperationCanceledException) es lo que efectivamente tira
        // Task.FromCanceled/await en cancelación -- ThrowsAnyAsync matea por tipo base, no exacto.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync("https://example.com/video", cts.Token));
    }

    private sealed class ThrowingRunner : IYtDlpProcessRunner
    {
        public Task<YtDlpProcessResult> RunAsync(
            string executablePath, IReadOnlyList<string> arguments, IProgress<string>? onOutputLine, CancellationToken ct)
            => Task.FromCanceled<YtDlpProcessResult>(ct.IsCancellationRequested ? ct : new CancellationToken(true));
    }
}
