using AudioTranscriber.Core.WebImport;

namespace AudioTranscriber.Core.Tests;

public class WebAudioDownloaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _fakeExePath;
    private readonly string _destDir;

    public WebAudioDownloaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_downloader_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _fakeExePath = Path.Combine(_dir, "yt-dlp.exe");
        File.WriteAllText(_fakeExePath, "fake");
        _destDir = Path.Combine(_dir, "dest");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeYtDlpProcessRunner : IYtDlpProcessRunner
    {
        public YtDlpProcessResult Result { get; set; } = new(0, string.Empty, string.Empty);
        public IReadOnlyList<string>? LastArguments { get; private set; }
        public IProgress<string>? LastOutputHandler { get; private set; }

        /// <summary>Si viene seteado, se crea este archivo en <paramref name="destDirWriter"/> antes de devolver el resultado -- simula que yt-dlp efectivamente bajó algo.</summary>
        public Action<string>? OnRun { get; set; }

        public Task<YtDlpProcessResult> RunAsync(
            string executablePath, IReadOnlyList<string> arguments, IProgress<string>? onOutputLine, CancellationToken ct)
        {
            LastArguments = arguments;
            LastOutputHandler = onOutputLine;
            OnRun?.Invoke(executablePath);
            return Task.FromResult(Result);
        }
    }

    private static WebMediaItem MakeItem(string title = "Mi Video", string? url = "https://example.com/watch?v=x") =>
        new(Index: 1, Id: "x", Title: title, Duration: TimeSpan.FromSeconds(60), Url: url);

    [Fact]
    public async Task Sin_yt_dlp_en_disco_devuelve_YtDlpNotAvailable()
    {
        var missingPath = Path.Combine(_dir, "no-existe.exe");
        var downloader = new WebAudioDownloader(new FakeYtDlpProcessRunner(), missingPath);

        var result = await downloader.DownloadAsync(
            MakeItem(), "https://example.com/watch?v=x", _destDir, progress: null);

        Assert.Equal(WebImportStatus.YtDlpNotAvailable, result.Status);
    }

    [Fact]
    public async Task Pide_bestaudio_sin_extract_audio_y_usa_la_url_del_item()
    {
        var runner = new FakeYtDlpProcessRunner
        {
            OnRun = _ => File.WriteAllText(Path.Combine(_destDir, "Mi Video.m4a"), "audio"),
        };
        var downloader = new WebAudioDownloader(runner, _fakeExePath);

        await downloader.DownloadAsync(MakeItem(), "https://example.com/watch?v=x", _destDir, progress: null);

        Assert.NotNull(runner.LastArguments);
        Assert.Contains("-f", runner.LastArguments!);
        Assert.Contains("bestaudio", runner.LastArguments!);
        Assert.DoesNotContain("--extract-audio", runner.LastArguments!);
        Assert.Contains("https://example.com/watch?v=x", runner.LastArguments!);
        Assert.DoesNotContain("--playlist-items", runner.LastArguments!);
    }

    [Fact]
    public async Task Item_sin_url_propia_usa_playlist_items_con_el_index_y_la_url_original()
    {
        var runner = new FakeYtDlpProcessRunner
        {
            OnRun = _ => File.WriteAllText(Path.Combine(_destDir, "Mi Video.m4a"), "audio"),
        };
        var downloader = new WebAudioDownloader(runner, _fakeExePath);
        var item = MakeItem(url: null) with { Index = 3 };

        await downloader.DownloadAsync(item, "https://example.com/playlist?list=abc", _destDir, progress: null);

        Assert.Contains("--playlist-items", runner.LastArguments!);
        Assert.Contains("3", runner.LastArguments!);
        Assert.Contains("https://example.com/playlist?list=abc", runner.LastArguments!);
    }

    [Fact]
    public async Task Descarga_exitosa_encuentra_el_archivo_final_sin_importar_la_extension()
    {
        var runner = new FakeYtDlpProcessRunner
        {
            OnRun = _ => File.WriteAllText(Path.Combine(_destDir, "Mi Video.webm"), "audio"),
        };
        var downloader = new WebAudioDownloader(runner, _fakeExePath);

        var result = await downloader.DownloadAsync(MakeItem(), "https://example.com/x", _destDir, progress: null);

        Assert.Equal(WebImportStatus.Success, result.Status);
        Assert.Equal(Path.Combine(_destDir, "Mi Video.webm"), result.FilePath);
    }

    [Fact]
    public async Task Exit_code_exitoso_pero_sin_archivo_en_disco_devuelve_YtDlpFailed()
    {
        var runner = new FakeYtDlpProcessRunner(); // no crea ningún archivo
        var downloader = new WebAudioDownloader(runner, _fakeExePath);

        var result = await downloader.DownloadAsync(MakeItem(), "https://example.com/x", _destDir, progress: null);

        Assert.Equal(WebImportStatus.YtDlpFailed, result.Status);
    }

    [Fact]
    public async Task Exit_code_de_error_se_clasifica_via_YtDlpErrorClassifier()
    {
        var runner = new FakeYtDlpProcessRunner
        {
            Result = new YtDlpProcessResult(1, string.Empty, "ERROR: Network is unreachable"),
        };
        var downloader = new WebAudioDownloader(runner, _fakeExePath);

        var result = await downloader.DownloadAsync(MakeItem(), "https://example.com/x", _destDir, progress: null);

        Assert.Equal(WebImportStatus.NoConnection, result.Status);
    }

    /// <summary>
    /// A propósito NO usamos <see cref="Progress{T}"/> acá: postea al <see cref="SynchronizationContext"/>
    /// capturado en el constructor (para poder marshalear a la UI real), lo que en el hilo del test
    /// (sin contexto propio) lo vuelve asincrónico vía thread pool -- el assert de abajo correría en
    /// carrera contra ese post. Este doble reporta sync, determinístico.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) => Reports.Add(value);
    }

    [Fact]
    public async Task Reporta_progreso_parseando_las_lineas_de_stdout()
    {
        var progress = new SyncProgress<WebDownloadProgress>();
        var runner = new FakeYtDlpProcessRunner
        {
            OnRun = _ => File.WriteAllText(Path.Combine(_destDir, "Mi Video.m4a"), "audio"),
        };
        var downloader = new WebAudioDownloader(runner, _fakeExePath);

        await downloader.DownloadAsync(MakeItem(), "https://example.com/x", _destDir, progress);

        Assert.NotNull(runner.LastOutputHandler);
        runner.LastOutputHandler!.Report("[download]  55.0% of 4.00MiB at 2.00MiB/s ETA 00:01");

        Assert.Contains(progress.Reports, p => Math.Abs(p.Percent - 55.0) < 0.01);
    }
}
