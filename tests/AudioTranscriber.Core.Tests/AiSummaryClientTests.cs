using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AiSummaryClientTests
{
    // Mismo patrón de FakeHandler que SyncApiClientTests: captura la request y devuelve una
    // respuesta canned, sin red real.
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);
            return _responder(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void BuildRequestBody_ArmaIdYForce()
    {
        var body = AiSummaryClient.BuildRequestBody("t1", force: true);

        Assert.Equal("""{"id":"t1","force":true}""", body);
    }

    [Fact]
    public void BuildRequestBody_ForceFalsePorDefault()
    {
        var body = AiSummaryClient.BuildRequestBody("t1", force: false);

        Assert.Equal("""{"id":"t1","force":false}""", body);
    }

    [Fact]
    public void ParseResponse_CasoCompleto()
    {
        var result = AiSummaryClient.ParseResponse(
            """{"summary":"Resumen breve.","keyPoints":["A","B"],"actionItems":["Hacer X"],"cached":true}""");

        Assert.Equal("Resumen breve.", result.Summary);
        Assert.Equal(new[] { "A", "B" }, result.KeyPoints);
        Assert.Equal(new[] { "Hacer X" }, result.ActionItems);
        Assert.True(result.Cached);
    }

    [Fact]
    public void ParseResponse_SinKeyPointsNiActionItems_DevuelveListasVacias()
    {
        var result = AiSummaryClient.ParseResponse("""{"summary":"Solo resumen.","cached":false}""");

        Assert.Empty(result.KeyPoints);
        Assert.Empty(result.ActionItems);
        Assert.False(result.Cached);
    }

    [Fact]
    public void ParseResponse_IgnoraItemsNoString()
    {
        var result = AiSummaryClient.ParseResponse(
            """{"summary":"x","keyPoints":["A",123,null,"B"],"actionItems":[]}""");

        Assert.Equal(new[] { "A", "B" }, result.KeyPoints);
    }

    [Fact]
    public void ParseResponse_SinSummary_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiSummaryClient.ParseResponse("""{"keyPoints":[]}"""));
    }

    [Fact]
    public void ParseResponse_JsonInvalido_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiSummaryClient.ParseResponse("no es json"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BuildErrorMessage_SesionInvalida_MensajeDeReLogin(HttpStatusCode status)
    {
        var msg = AiSummaryClient.BuildErrorMessage(status, """{"error":"unauthorized"}""");

        Assert.Contains("Iniciá sesión de nuevo", msg);
    }

    [Fact]
    public void BuildErrorMessage_TooManyRequests_UsaMensajeDelBackend()
    {
        var msg = AiSummaryClient.BuildErrorMessage(
            HttpStatusCode.TooManyRequests, """{"error":"Llegaste al límite diario de resúmenes con IA. Probá mañana."}""");

        Assert.Equal("Llegaste al límite diario de resúmenes con IA. Probá mañana.", msg);
    }

    [Fact]
    public void BuildErrorMessage_Generico_IncluyeStatusYDetalle()
    {
        var msg = AiSummaryClient.BuildErrorMessage(HttpStatusCode.InternalServerError, """{"error":"boom"}""");

        Assert.Contains("500", msg);
        Assert.Contains("boom", msg);
    }

    [Fact]
    public async Task SummarizeAsync_MandaBearerYBodyCorrecto_ParseaOk()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"summary":"ok","keyPoints":[],"actionItems":[],"cached":false}"""));
        var client = new AiSummaryClient(new HttpClient(handler), "https://backend.test/");

        var result = await client.SummarizeAsync("t1", force: false, "AT", CancellationToken.None);

        Assert.Equal("ok", result.Summary);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Equal("https://backend.test/api/summarize", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("""{"id":"t1","force":false}""", handler.LastBody);
    }

    [Fact]
    public async Task SummarizeAsync_ErrorHttp_LanzaConMensajeAmigable()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"No se encontró la transcripción."}""", HttpStatusCode.NotFound));
        var client = new AiSummaryClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.SummarizeAsync("t1", false, "AT", CancellationToken.None));
        Assert.Contains("no se encontró", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SummarizeAsync_SinAccessToken_Lanza()
    {
        var client = new AiSummaryClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SummarizeAsync("t1", false, "", CancellationToken.None));
    }
}
