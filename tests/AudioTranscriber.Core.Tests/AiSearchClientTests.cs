using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AiSearchClientTests
{
    // Mismo patrón de FakeHandler que AiRecipesClientTests/AiChatClientTests.
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---- BuildQueryUrl (pura) ----------------------------------------------------------------

    [Fact]
    public void BuildQueryUrl_ArmaUrlConQEncoded()
    {
        var url = AiSearchClient.BuildQueryUrl("https://backend.test/", "café con leche");

        Assert.Equal("https://backend.test/api/notes/search?q=caf%C3%A9%20con%20leche", url);
    }

    // ---- ParseResponse (pura) -----------------------------------------------------------------

    [Fact]
    public void ParseResponse_CasoCompleto()
    {
        var results = AiSearchClient.ParseResponse(
            """{"results":[{"id":"n1","title":"Reunión","createdAt":"2026-07-01T10:00:00Z","projectId":"p1","snippet":"...hablamos de..."}]}""");

        Assert.Single(results);
        Assert.Equal("n1", results[0].Id);
        Assert.Equal("Reunión", results[0].Title);
        Assert.Equal("2026-07-01T10:00:00Z", results[0].CreatedAt);
        Assert.Equal("p1", results[0].ProjectId);
        Assert.Equal("...hablamos de...", results[0].Snippet);
    }

    [Fact]
    public void ParseResponse_ProjectIdNull_DevuelveNull()
    {
        var results = AiSearchClient.ParseResponse(
            """{"results":[{"id":"n1","title":"Suelta","createdAt":"2026-07-01T10:00:00Z","projectId":null,"snippet":""}]}""");

        Assert.Null(results[0].ProjectId);
    }

    [Fact]
    public void ParseResponse_Vacio()
    {
        Assert.Empty(AiSearchClient.ParseResponse("""{"results":[]}"""));
    }

    [Fact]
    public void ParseResponse_JsonInvalido_DevuelveVacioSinLanzar()
    {
        Assert.Empty(AiSearchClient.ParseResponse("no es json"));
    }

    [Fact]
    public void ParseResponse_ItemSinId_SeIgnoraElItem()
    {
        var results = AiSearchClient.ParseResponse(
            """{"results":[{"id":"n1","title":"OK","createdAt":"x","snippet":""},{"title":"SinId","createdAt":"x","snippet":""}]}""");

        Assert.Single(results);
        Assert.Equal("n1", results[0].Id);
    }

    // ---- BuildErrorMessage ----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BuildErrorMessage_SesionInvalida_MensajeDeReLogin(HttpStatusCode status)
    {
        Assert.Contains("Iniciá sesión de nuevo", AiSearchClient.BuildErrorMessage(status, "{}"));
    }

    [Fact]
    public void BuildErrorMessage_Generico_IncluyeStatusYDetalle()
    {
        var msg = AiSearchClient.BuildErrorMessage(HttpStatusCode.InternalServerError, """{"error":"No se pudo buscar en tus notas."}""");
        Assert.Contains("500", msg);
        Assert.Contains("No se pudo buscar en tus notas.", msg);
    }

    // ---- SearchAsync ------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_MandaBearerYUrlCorrecta()
    {
        var handler = new FakeHandler(_ => Json("""{"results":[]}"""));
        var client = new AiSearchClient(new HttpClient(handler), "https://backend.test/");

        await client.SearchAsync("reunión", "AT", CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest.Headers.Authorization.Parameter);
        // AbsoluteUri (no ToString()): Uri.ToString() des-escapa caracteres no-ASCII "legibles"
        // para mostrar, AbsoluteUri conserva el %-encoding real que viaja por la red.
        Assert.Equal("https://backend.test/api/notes/search?q=reuni%C3%B3n", handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SearchAsync_ParseaResultados()
    {
        var handler = new FakeHandler(_ => Json("""{"results":[{"id":"n1","title":"T","createdAt":"x","projectId":null,"snippet":"s"}]}"""));
        var client = new AiSearchClient(new HttpClient(handler), "https://backend.test");

        var results = await client.SearchAsync("t", "AT", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("n1", results[0].Id);
    }

    [Fact]
    public async Task SearchAsync_QueryVacio_NoLlamaAlServidor()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no debería llamar al servidor"));
        var client = new AiSearchClient(new HttpClient(handler), "https://backend.test");

        var results = await client.SearchAsync("   ", "AT", CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ErrorHttp_Lanza()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Necesitás iniciar sesión."}""", HttpStatusCode.Unauthorized));
        var client = new AiSearchClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.SearchAsync("t", "AT", CancellationToken.None));

        Assert.Contains("Iniciá sesión de nuevo", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_SinAccessToken_Lanza()
    {
        var client = new AiSearchClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SearchAsync("t", "", CancellationToken.None));
    }
}
