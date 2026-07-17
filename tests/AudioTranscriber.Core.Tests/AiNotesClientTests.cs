using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AiNotesClientTests
{
    // Mismo patrón de FakeHandler que AiRecipesClientTests/AiChatClientTests.
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

    // ---- BuildCreateRequestBody -----------------------------------------------------------------

    [Fact]
    public void BuildCreateRequestBody_ArmaText()
    {
        Assert.Equal("""{"text":"Hola mundo"}""", AiNotesClient.BuildCreateRequestBody("Hola mundo"));
    }

    // ---- ParseCreateResponse --------------------------------------------------------------------

    [Fact]
    public void ParseCreateResponse_CasoCompleto()
    {
        var note = AiNotesClient.ParseCreateResponse("""{"id":"n1","title":"Nota del chat"}""");

        Assert.Equal("n1", note.Id);
        Assert.Equal("Nota del chat", note.Title);
    }

    [Fact]
    public void ParseCreateResponse_SinTitle_TituloVacio()
    {
        var note = AiNotesClient.ParseCreateResponse("""{"id":"n1"}""");

        Assert.Equal("n1", note.Id);
        Assert.Equal(string.Empty, note.Title);
    }

    [Fact]
    public void ParseCreateResponse_SinId_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiNotesClient.ParseCreateResponse("""{"title":"x"}"""));
    }

    [Fact]
    public void ParseCreateResponse_JsonInvalido_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiNotesClient.ParseCreateResponse("no es json"));
    }

    // ---- BuildErrorMessage -----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BuildErrorMessage_SesionInvalida_MensajeDeReLogin(HttpStatusCode status)
    {
        Assert.Contains("Iniciá sesión de nuevo", AiNotesClient.BuildErrorMessage(status, "{}"));
    }

    [Fact]
    public void BuildErrorMessage_BadRequest_UsaDetalleDelServidor()
    {
        var msg = AiNotesClient.BuildErrorMessage(HttpStatusCode.BadRequest, """{"error":"No hay contenido para guardar."}""");
        Assert.Equal("No hay contenido para guardar.", msg);
    }

    [Fact]
    public void BuildErrorMessage_LimiteDiario_UsaDetalleDelServidor()
    {
        var msg = AiNotesClient.BuildErrorMessage(HttpStatusCode.TooManyRequests,
            """{"error":"Llegaste al límite diario de transcripciones. Probá mañana o escribinos."}""");
        Assert.Equal("Llegaste al límite diario de transcripciones. Probá mañana o escribinos.", msg);
    }

    // ---- CreateNoteAsync -------------------------------------------------------------------------

    [Fact]
    public async Task CreateNoteAsync_MandaBearerYBodyCorrecto_ParseaLaNotaCreada()
    {
        var handler = new FakeHandler(_ => Json("""{"id":"n1","title":"Nota del chat"}"""));
        var client = new AiNotesClient(new HttpClient(handler), "https://backend.test/");

        var note = await client.CreateNoteAsync("Hola mundo", "AT", CancellationToken.None);

        Assert.Equal("n1", note.Id);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("https://backend.test/api/notes", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("""{"text":"Hola mundo"}""", handler.LastBody);
    }

    [Fact]
    public async Task CreateNoteAsync_ErrorHttp_LanzaConMensajeDelServidor()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"No hay contenido para guardar."}""", HttpStatusCode.BadRequest));
        var client = new AiNotesClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(() => client.CreateNoteAsync("", "AT", CancellationToken.None));
        Assert.Equal("No hay contenido para guardar.", ex.Message);
    }

    [Fact]
    public async Task CreateNoteAsync_SinAccessToken_Lanza()
    {
        var client = new AiNotesClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateNoteAsync("Hola", "", CancellationToken.None));
    }
}
