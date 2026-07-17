using System.Net;
using System.Text;
using AudioTranscriber.Core.Transcription;

namespace AudioTranscriber.Core.Tests;

public class CloudTranscriptionServiceTests : IDisposable
{
    private readonly string _dir;

    public CloudTranscriptionServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_cloud_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // Handler falso que captura la request y devuelve una respuesta canned (mismo patrón que
    // SyncApiClientTests.FakeHandler).
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastMultipartModelField { get; private set; }
        public string? LastMultipartModeField { get; private set; }
        public string? LastMultipartTargetLanguageField { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (name == "model")
                        LastMultipartModelField = await part.ReadAsStringAsync(ct);
                    else if (name == "mode")
                        LastMultipartModeField = await part.ReadAsStringAsync(ct);
                    else if (name == "targetLanguage")
                        LastMultipartTargetLanguageField = await part.ReadAsStringAsync(ct);
                }
            }
            return _responder(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private string WriteAudioFile(string name = "audio.ogg")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    // ---- ResolveModel (lógica pura) ---------------------------------------------------

    [Theory]
    [InlineData("whisper-large-v3")]
    [InlineData("whisper-large-v3-turbo")]
    public void ResolveModel_ModeloEnLaAllowlist_LoDevuelveTalCual(string model)
    {
        Assert.Equal(model, CloudTranscriptionService.ResolveModel(model));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("whisper-1")]
    [InlineData("otro-modelo-cualquiera")]
    public void ResolveModel_ModeloFueraDeLaAllowlistONulo_CaeAlDefault(string? model)
    {
        Assert.Equal(CloudTranscriptionService.DefaultModel, CloudTranscriptionService.ResolveModel(model));
    }

    // ---- ParseResponse (lógica pura) --------------------------------------------------

    [Fact]
    public void ParseResponse_JsonValido_DevuelveTextoEId()
    {
        var result = CloudTranscriptionService.ParseResponse("""{"text":"hola mundo","id":"abc-123"}""");

        Assert.Equal("hola mundo", result.Text);
        Assert.Equal("abc-123", result.Id);
    }

    [Fact]
    public void ParseResponse_SinId_IdQuedaNulo()
    {
        var result = CloudTranscriptionService.ParseResponse("""{"text":"hola"}""");

        Assert.Equal("hola", result.Text);
        Assert.Null(result.Id);
    }

    [Fact]
    public void ParseResponse_ConTranslationWarning_LoParsea()
    {
        var result = CloudTranscriptionService.ParseResponse(
            """{"text":"hola","translationWarning":"No se pudo traducir, pero se guardó la transcripción original: timeout"}""");

        Assert.Equal("No se pudo traducir, pero se guardó la transcripción original: timeout", result.TranslationWarning);
    }

    [Fact]
    public void ParseResponse_SinTranslationWarning_QuedaNulo()
    {
        var result = CloudTranscriptionService.ParseResponse("""{"text":"hola"}""");

        Assert.Null(result.TranslationWarning);
    }

    [Fact]
    public void ParseResponse_TextoConEspacios_SeRecorta()
    {
        var result = CloudTranscriptionService.ParseResponse("""{"text":"  hola  "}""");

        Assert.Equal("hola", result.Text);
    }

    [Fact]
    public void ParseResponse_SinCampoText_Lanza()
    {
        var ex = Assert.Throws<CloudTranscriptionException>(
            () => CloudTranscriptionService.ParseResponse("""{"id":"abc"}"""));
        Assert.Contains("texto transcripto", ex.Message);
    }

    [Fact]
    public void ParseResponse_JsonInvalido_Lanza()
    {
        Assert.Throws<CloudTranscriptionException>(() => CloudTranscriptionService.ParseResponse("no es json"));
    }

    // ---- BuildErrorMessage (lógica pura) -----------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BuildErrorMessage_401o403_MensajeDeSesion(HttpStatusCode status)
    {
        var msg = CloudTranscriptionService.BuildErrorMessage(status, """{"error":"invalid token"}""");
        Assert.Contains("sesión", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildErrorMessage_413_MensajeDeArchivoGrande()
    {
        var msg = CloudTranscriptionService.BuildErrorMessage(HttpStatusCode.RequestEntityTooLarge, "");
        Assert.Contains("25 MB", msg);
    }

    [Fact]
    public void BuildErrorMessage_OtroStatus_MensajeGenericoConDetalleDelBody()
    {
        var msg = CloudTranscriptionService.BuildErrorMessage(
            HttpStatusCode.InternalServerError, """{"error":"Groq rate limit"}""");

        Assert.Contains("500", msg);
        Assert.Contains("Groq rate limit", msg);
    }

    // ---- TranscribeAsync: validaciones sin red -----------------------------------------

    [Fact]
    public async Task TranscribeAsync_SinAccessToken_Lanza()
    {
        var svc = new CloudTranscriptionService(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://x.com");
        var path = WriteAudioFile();

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.TranscribeAsync(path, "", "whisper-large-v3", null, CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_ArchivoInexistente_Lanza()
    {
        var svc = new CloudTranscriptionService(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://x.com");

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => svc.TranscribeAsync(Path.Combine(_dir, "no_existe.ogg"), "token", "whisper-large-v3", null, CancellationToken.None));
    }

    // ---- TranscribeAsync: request/response vía FakeHandler -----------------------------

    [Fact]
    public async Task TranscribeAsync_ArmaElRequestCorrecto_BearerYModel()
    {
        var handler = new FakeHandler(_ => Json("""{"text":"transcripto ok","id":"t1"}"""));
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com/");
        var path = WriteAudioFile();

        var text = await svc.TranscribeAsync(path, "sess-token", "whisper-large-v3-turbo", null, CancellationToken.None);

        Assert.Equal("transcripto ok", text);
        Assert.Equal("https://backend.example.com/api/transcribe", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sess-token", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("whisper-large-v3-turbo", handler.LastMultipartModelField);
    }

    [Fact]
    public async Task TranscribeAsync_ModeloNoPermitido_MandaElDefaultEnElRequest()
    {
        var handler = new FakeHandler(_ => Json("""{"text":"ok"}"""));
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com");
        var path = WriteAudioFile();

        await svc.TranscribeAsync(path, "sess-token", "modelo-raro", null, CancellationToken.None);

        Assert.Equal(CloudTranscriptionService.DefaultModel, handler.LastMultipartModelField);
    }

    [Fact]
    public async Task TranscribeAsync_ErrorDelBackend_LanzaConMensajeAmigable()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Token expirado."}""", HttpStatusCode.Unauthorized));
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com");
        var path = WriteAudioFile();

        var ex = await Assert.ThrowsAsync<CloudTranscriptionException>(
            () => svc.TranscribeAsync(path, "sess-token", "whisper-large-v3", null, CancellationToken.None));

        Assert.Contains("sesión", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribeAsync_ReportaProgresoDeSubidaYRecepcion()
    {
        var handler = new FakeHandler(_ => Json("""{"text":"ok"}"""));
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com");
        var path = WriteAudioFile();
        var logs = new List<string>();
        var progress = new Progress<string>(logs.Add);

        await svc.TranscribeAsync(path, "sess-token", "whisper-large-v3", progress, CancellationToken.None);
        await Task.Delay(20); // Progress<T> marshalea via SynchronizationContext/ThreadPool

        Assert.Contains(logs, l => l.Contains("Subiendo"));
        Assert.Contains(logs, l => l.Contains("recibida"));
    }

    // ---- Fix 2026-07-10 (LOW): timeout de HttpClient ya no se muestra como "cancelado" ----------

    [Fact]
    public async Task TranscribeAsync_TimeoutDelHttpClient_LanzaCloudTranscriptionExceptionConMensajeDeTimeout()
    {
        // Simula el timeout de 100s del HttpClient compartido: SendAsync tira
        // OperationCanceledException/TaskCanceledException SIN que el CancellationToken del caller
        // haya sido cancelado (CancellationToken.None acá representa "el usuario nunca pidió
        // cancelar"). Antes esto se propagaba tal cual hasta MainViewModel, que lo mostraba como
        // "Transcripción cancelada." -- un mensaje falso, el usuario nunca canceló nada.
        var handler = new FakeHandler(_ => throw new TaskCanceledException("Simulated HttpClient timeout"));
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com");
        var path = WriteAudioFile();

        var ex = await Assert.ThrowsAsync<CloudTranscriptionException>(
            () => svc.TranscribeAsync(path, "sess-token", "whisper-large-v3", null, CancellationToken.None));

        Assert.Contains("tardó demasiado", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribeAsync_CancelacionRealDelUsuario_SigueSiendoOperationCanceledException()
    {
        // Cuando SÍ es el usuario quien cancela (su propio CancellationToken pedido), el fix NO debe
        // convertirlo en CloudTranscriptionException -- MainViewModel tiene que poder seguir
        // mostrando "Transcripción cancelada." como corresponde.
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com");
        var path = WriteAudioFile();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.TranscribeAsync(path, "sess-token", "whisper-large-v3", null, cts.Token));
    }

    // ---- Traducir (2026-07-14): mode/targetLanguage en el request -------------------------

    [Fact]
    public async Task TranscribeAsync_TranslateFalse_NoMandaModeNiTargetLanguage()
    {
        // Default (sin pedir traducir): el request queda IDÉNTICO al de antes de esta feature --
        // el backend ya default-ea a "transcribe" cuando el campo no viene.
        var handler = new FakeHandler(_ => Json("""{"text":"ok"}"""));
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com");
        var path = WriteAudioFile();

        await svc.TranscribeAsync(path, "sess-token", "whisper-large-v3", null, CancellationToken.None);

        Assert.Null(handler.LastMultipartModeField);
        Assert.Null(handler.LastMultipartTargetLanguageField);
    }

    [Fact]
    public async Task TranscribeAsync_TranslateTrue_MandaModeTranslateYElIdiomaPedido()
    {
        var handler = new FakeHandler(_ => Json("""{"text":"ok"}"""));
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com");
        var path = WriteAudioFile();

        await svc.TranscribeAsync(
            path, "sess-token", "whisper-large-v3", null, CancellationToken.None,
            translate: true, targetLanguage: "pt");

        Assert.Equal("translate", handler.LastMultipartModeField);
        Assert.Equal("pt", handler.LastMultipartTargetLanguageField);
    }

    [Fact]
    public async Task TranscribeAsync_TranslateTrueConIdiomaInvalido_CaeAlDefault()
    {
        var handler = new FakeHandler(_ => Json("""{"text":"ok"}"""));
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com");
        var path = WriteAudioFile();

        await svc.TranscribeAsync(
            path, "sess-token", "whisper-large-v3", null, CancellationToken.None,
            translate: true, targetLanguage: "idioma-inventado");

        Assert.Equal("translate", handler.LastMultipartModeField);
        Assert.Equal(TranslationOptions.DefaultLanguage, handler.LastMultipartTargetLanguageField);
    }

    [Fact]
    public async Task TranscribeAsync_TranslationWarning_SeReportaPorOnLog()
    {
        var handler = new FakeHandler(_ => Json(
            """{"text":"transcripcion original","translationWarning":"No se pudo traducir: timeout"}"""));
        var svc = new CloudTranscriptionService(new HttpClient(handler), "https://backend.example.com");
        var path = WriteAudioFile();
        var logs = new List<string>();
        var progress = new Progress<string>(logs.Add);

        var text = await svc.TranscribeAsync(
            path, "sess-token", "whisper-large-v3", progress, CancellationToken.None,
            translate: true, targetLanguage: "en");
        await Task.Delay(20); // Progress<T> marshalea via SynchronizationContext/ThreadPool

        Assert.Equal("transcripcion original", text);
        Assert.Contains(logs, l => l.Contains("No se pudo traducir"));
    }
}
