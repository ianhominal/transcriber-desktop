using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AiPolishClientTests
{
    // Mismo patrón de FakeHandler que AiSummaryClientTests: captura la request y devuelve una
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
    public void BuildRequestBody_ConTranscriptionId()
    {
        var body = AiPolishClient.BuildRequestBody("hola mundo", "t1");

        Assert.Equal("""{"text":"hola mundo","transcriptionId":"t1"}""", body);
    }

    [Fact]
    public void BuildRequestBody_SinTranscriptionId_MandaVacio()
    {
        var body = AiPolishClient.BuildRequestBody("hola mundo", null);

        Assert.Equal("""{"text":"hola mundo","transcriptionId":""}""", body);
    }

    [Fact]
    public void ParseResponse_CasoCompleto()
    {
        var result = AiPolishClient.ParseResponse(
            """{"text":"Texto pulido.","polishedChunks":3,"totalChunks":3}""");

        Assert.Equal("Texto pulido.", result.Text);
        Assert.Equal(3, result.PolishedChunks);
        Assert.Equal(3, result.TotalChunks);
    }

    [Fact]
    public void ParseResponse_PulidoParcial_PolishedChunksMenorQueTotalChunks()
    {
        var result = AiPolishClient.ParseResponse(
            """{"text":"Un tramo largo, con una parte sin pulir.","polishedChunks":2,"totalChunks":3}""");

        Assert.Equal("Un tramo largo, con una parte sin pulir.", result.Text);
        Assert.Equal(2, result.PolishedChunks);
        Assert.Equal(3, result.TotalChunks);
        Assert.True(result.PolishedChunks < result.TotalChunks);
    }

    [Fact]
    public void ParseResponse_SinChunks_DevuelveCero()
    {
        var result = AiPolishClient.ParseResponse("""{"text":"x"}""");

        Assert.Equal(0, result.PolishedChunks);
        Assert.Equal(0, result.TotalChunks);
    }

    [Fact]
    public void ParseResponse_SinText_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiPolishClient.ParseResponse("""{"polishedChunks":1,"totalChunks":1}"""));
    }

    [Fact]
    public void ParseResponse_JsonInvalido_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiPolishClient.ParseResponse("no es json"));
    }

    [Fact]
    public void BuildErrorMessage_UsaElMensajeDelServidorTalCual()
    {
        var msg = AiPolishClient.BuildErrorMessage(
            HttpStatusCode.InternalServerError, """{"error":"No pudimos mejorar el texto. Probá de nuevo."}""");

        Assert.Equal("No pudimos mejorar el texto. Probá de nuevo.", msg);
    }

    [Fact]
    public void BuildErrorMessage_Unauthorized_NoLoReescribe_UsaElDelServidor()
    {
        // A diferencia de AiSummaryClient/AiNotesClient (que hardcodean un mensaje propio para
        // 401/403 sin importar el cuerpo), acá el servidor YA manda el mensaje humano -- el cliente
        // NO debe reescribirlo.
        var msg = AiPolishClient.BuildErrorMessage(
            HttpStatusCode.Unauthorized, """{"error":"Tu sesión expiró. Iniciá sesión de nuevo."}""");

        Assert.Equal("Tu sesión expiró. Iniciá sesión de nuevo.", msg);
    }

    [Fact]
    public void BuildErrorMessage_CuerpoSinError_UsaRespaldoGenericoConStatus()
    {
        var msg = AiPolishClient.BuildErrorMessage(HttpStatusCode.InternalServerError, "<html>Bad Gateway</html>");

        Assert.Contains("500", msg);
    }

    [Fact]
    public async Task PolishAsync_MandaBearerYBodyCorrecto_ParseaOk()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"text":"pulido","polishedChunks":1,"totalChunks":1}"""));
        var client = new AiPolishClient(new HttpClient(handler), "https://backend.test/");

        var result = await client.PolishAsync("crudo", "t1", "AT", CancellationToken.None);

        Assert.Equal("pulido", result.Text);
        Assert.Equal(1, result.PolishedChunks);
        Assert.Equal(1, result.TotalChunks);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Equal("https://backend.test/api/polish", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("""{"text":"crudo","transcriptionId":"t1"}""", handler.LastBody);
    }

    [Fact]
    public async Task PolishAsync_SinTranscriptionId_MandaVacioYParseaOk()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"text":"pulido","polishedChunks":1,"totalChunks":1}"""));
        var client = new AiPolishClient(new HttpClient(handler), "https://backend.test");

        await client.PolishAsync("crudo", null, "AT", CancellationToken.None);

        Assert.Equal("""{"text":"crudo","transcriptionId":""}""", handler.LastBody);
    }

    [Fact]
    public async Task PolishAsync_PulidoParcial_DevuelveChunksMenoresSinPerderTexto()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"text":"pulido parcial, un tramo quedó como estaba","polishedChunks":2,"totalChunks":3}"""));
        var client = new AiPolishClient(new HttpClient(handler), "https://backend.test");

        var result = await client.PolishAsync("crudo largo", null, "AT", CancellationToken.None);

        Assert.True(result.PolishedChunks < result.TotalChunks);
        Assert.Equal("pulido parcial, un tramo quedó como estaba", result.Text);
    }

    [Fact]
    public async Task PolishAsync_ErrorHttp_LanzaConMensajeDelServidor()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"No hay texto para mejorar."}""", HttpStatusCode.BadRequest));
        var client = new AiPolishClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.PolishAsync("", null, "AT", CancellationToken.None));
        Assert.Equal("No hay texto para mejorar.", ex.Message);
    }

    [Fact]
    public async Task PolishAsync_Unauthorized_LanzaConMensajeDelServidor()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"error":"Tu sesión expiró. Iniciá sesión de nuevo."}""", HttpStatusCode.Unauthorized));
        var client = new AiPolishClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.PolishAsync("texto", null, "AT", CancellationToken.None));
        Assert.Equal("Tu sesión expiró. Iniciá sesión de nuevo.", ex.Message);
    }

    [Fact]
    public async Task PolishAsync_RespuestaMalformada_Lanza()
    {
        var handler = new FakeHandler(_ => Json("no es json"));
        var client = new AiPolishClient(new HttpClient(handler), "https://backend.test");

        await Assert.ThrowsAsync<AiAssistException>(
            () => client.PolishAsync("texto", null, "AT", CancellationToken.None));
    }

    [Fact]
    public async Task PolishAsync_SinAccessToken_Lanza()
    {
        var client = new AiPolishClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PolishAsync("texto", null, "", CancellationToken.None));
    }
}
