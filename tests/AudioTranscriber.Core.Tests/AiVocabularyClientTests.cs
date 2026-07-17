using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AiVocabularyClientTests
{
    // Mismo patrón de FakeHandler que AiRecipesClientTests: captura la request y devuelve una
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

    // ---- ParseListResponse ----------------------------------------------------------------

    [Fact]
    public void ParseListResponse_CasoCompleto()
    {
        var terms = AiVocabularyClient.ParseListResponse(
            """{"terms":[{"id":"v1","term":"Rioplatense","createdAt":"2026-01-01"}]}""");

        Assert.Single(terms);
        Assert.Equal("v1", terms[0].Id);
        Assert.Equal("Rioplatense", terms[0].Term);
        Assert.Equal("2026-01-01", terms[0].CreatedAt);
    }

    [Fact]
    public void ParseListResponse_Vacio()
    {
        Assert.Empty(AiVocabularyClient.ParseListResponse("""{"terms":[]}"""));
    }

    [Fact]
    public void ParseListResponse_JsonInvalido_DevuelveVacioSinLanzar()
    {
        Assert.Empty(AiVocabularyClient.ParseListResponse("no es json"));
    }

    [Fact]
    public void ParseListResponse_ItemSinIdOTermino_SeIgnoraElItem()
    {
        var terms = AiVocabularyClient.ParseListResponse(
            """{"terms":[{"id":"v1","term":"OK"},{"term":"SinId"},{"id":"v2"}]}""");

        Assert.Single(terms);
        Assert.Equal("v1", terms[0].Id);
    }

    // ---- ParseTermResponse ------------------------------------------------------------------

    [Fact]
    public void ParseTermResponse_CasoCompleto()
    {
        var term = AiVocabularyClient.ParseTermResponse("""{"term":{"id":"v1","term":"Lugloc","createdAt":"2026-01-01"}}""");

        Assert.Equal("v1", term.Id);
        Assert.Equal("Lugloc", term.Term);
    }

    [Fact]
    public void ParseTermResponse_SinTerm_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiVocabularyClient.ParseTermResponse("""{"ok":true}"""));
    }

    [Fact]
    public void ParseTermResponse_JsonInvalido_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiVocabularyClient.ParseTermResponse("no es json"));
    }

    // ---- BuildSaveRequestBody / BuildErrorMessage --------------------------------------------

    [Fact]
    public void BuildSaveRequestBody_ArmaTerm()
    {
        Assert.Equal("""{"term":"Lugloc"}""", AiVocabularyClient.BuildSaveRequestBody("Lugloc"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BuildErrorMessage_SesionInvalida_MensajeDeReLogin(HttpStatusCode status)
    {
        Assert.Contains("Iniciá sesión de nuevo", AiVocabularyClient.BuildErrorMessage(status, "{}"));
    }

    [Fact]
    public void BuildErrorMessage_Conflict_UsaDetalleDelServidor()
    {
        var msg = AiVocabularyClient.BuildErrorMessage(HttpStatusCode.Conflict, """{"error":"Ese término ya está en tu vocabulario."}""");
        Assert.Equal("Ese término ya está en tu vocabulario.", msg);
    }

    [Fact]
    public void BuildErrorMessage_BadRequest_UsaDetalleDelServidor()
    {
        var msg = AiVocabularyClient.BuildErrorMessage(HttpStatusCode.BadRequest, """{"error":"El término no puede estar vacío ni superar los 80 caracteres."}""");
        Assert.Equal("El término no puede estar vacío ni superar los 80 caracteres.", msg);
    }

    [Fact]
    public void BuildErrorMessage_Generico_IncluyeStatusYDetalle()
    {
        var msg = AiVocabularyClient.BuildErrorMessage(HttpStatusCode.InternalServerError, """{"error":"boom"}""");

        Assert.Contains("500", msg);
        Assert.Contains("boom", msg);
    }

    // ---- ListTermsAsync -----------------------------------------------------------------------

    [Fact]
    public async Task ListTermsAsync_MandaBearer_ParseaOk()
    {
        var handler = new FakeHandler(_ => Json("""{"terms":[{"id":"v1","term":"Lugloc","createdAt":"2026-01-01"}]}"""));
        var client = new AiVocabularyClient(new HttpClient(handler), "https://backend.test/");

        var terms = await client.ListTermsAsync("AT", CancellationToken.None);

        Assert.Single(terms);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("https://backend.test/api/vocabulary", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task ListTermsAsync_ErrorHttp_Lanza()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Necesitás iniciar sesión."}""", HttpStatusCode.Unauthorized));
        var client = new AiVocabularyClient(new HttpClient(handler), "https://backend.test");

        await Assert.ThrowsAsync<AiAssistException>(() => client.ListTermsAsync("AT", CancellationToken.None));
    }

    [Fact]
    public async Task ListTermsAsync_SinAccessToken_Lanza()
    {
        var client = new AiVocabularyClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListTermsAsync("", CancellationToken.None));
    }

    // ---- AddTermAsync ---------------------------------------------------------------------------

    [Fact]
    public async Task AddTermAsync_MandaBearerYBodyCorrecto_ParseaElTermCreado()
    {
        var handler = new FakeHandler(_ => Json("""{"term":{"id":"v1","term":"Lugloc","createdAt":"2026-01-01"}}""", HttpStatusCode.Created));
        var client = new AiVocabularyClient(new HttpClient(handler), "https://backend.test/");

        var term = await client.AddTermAsync("Lugloc", "AT", CancellationToken.None);

        Assert.Equal("v1", term.Id);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("AT", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Equal("https://backend.test/api/vocabulary", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("""{"term":"Lugloc"}""", handler.LastBody);
    }

    [Fact]
    public async Task AddTermAsync_Duplicado_LanzaConMensajeAmigable()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Ese término ya está en tu vocabulario."}""", HttpStatusCode.Conflict));
        var client = new AiVocabularyClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(() => client.AddTermAsync("Lugloc", "AT", CancellationToken.None));
        Assert.Equal("Ese término ya está en tu vocabulario.", ex.Message);
    }

    // ---- UpdateTermAsync ------------------------------------------------------------------------

    [Fact]
    public async Task UpdateTermAsync_MandaPatchAlIdCorrecto()
    {
        var handler = new FakeHandler(_ => Json("""{"term":{"id":"v1","term":"Editado","createdAt":"2026-01-01"}}"""));
        var client = new AiVocabularyClient(new HttpClient(handler), "https://backend.test");

        var term = await client.UpdateTermAsync("v1", "Editado", "AT", CancellationToken.None);

        Assert.Equal("Editado", term.Term);
        Assert.Equal(HttpMethod.Patch, handler.LastRequest!.Method);
        Assert.Equal("https://backend.test/api/vocabulary/v1", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("""{"term":"Editado"}""", handler.LastBody);
    }

    // ---- DeleteTermAsync ------------------------------------------------------------------------

    [Fact]
    public async Task DeleteTermAsync_MandaDeleteAlIdCorrecto()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true}"""));
        var client = new AiVocabularyClient(new HttpClient(handler), "https://backend.test");

        await client.DeleteTermAsync("v1", "AT", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("https://backend.test/api/vocabulary/v1", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task DeleteTermAsync_ErrorHttp_Lanza()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError));
        var client = new AiVocabularyClient(new HttpClient(handler), "https://backend.test");

        await Assert.ThrowsAsync<AiAssistException>(() => client.DeleteTermAsync("v1", "AT", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteTermAsync_SinAccessToken_Lanza()
    {
        var client = new AiVocabularyClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteTermAsync("v1", "", CancellationToken.None));
    }
}
