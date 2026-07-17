using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AiRecipesClientTests
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

    /// <summary>
    /// Stream de test que devuelve los "chunks" pasados UNO POR CADA llamada a Read/ReadAsync --
    /// simula cómo llega un streaming real de a pedazos (a diferencia de un MemoryStream, que un
    /// StreamReader podría leer entero de una sola vez), para poder verificar que
    /// <see cref="AiRecipesClient.ApplyRecipeAsync"/> reporta el ACUMULADO en cada porción.
    /// </summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        public ChunkedStream(IEnumerable<string> chunks) => _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunks.Count == 0) return 0;
            var chunk = _chunks.Dequeue();
            Array.Copy(chunk, 0, buffer, offset, chunk.Length);
            return chunk.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(Read(buffer, offset, count));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// <see cref="IProgress{T}"/> SINCRÓNICO para test: <see cref="System.Progress{T}"/> real
    /// siempre marshalea el callback vía <c>SynchronizationContext.Post</c> (asíncrono respecto de
    /// quien llama a <c>Report</c>, aunque no haya ningún contexto capturado -- cae al contexto
    /// default, que postea al ThreadPool), así que un assert inmediatamente después del `await` en
    /// un test de consola puede correr ANTES de que los callbacks se hayan procesado (carrera real,
    /// no hipotética: así falló la primera versión de este test). Este fake invoca el callback
    /// DIRECTO, en el mismo hilo que llama a <c>Report</c>, para poder asertar determinísticamente.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        public SyncProgress(Action<T> callback) => _callback = callback;
        public void Report(T value) => _callback(value);
    }

    // ---- ParseListResponse ----------------------------------------------------------------

    [Fact]
    public void ParseListResponse_CasoCompleto()
    {
        var recipes = AiRecipesClient.ParseListResponse(
            """{"recipes":[{"id":"r1","name":"Acta","instruction":"Armá un acta","isDefault":true,"createdAt":"2026-01-01"}]}""");

        Assert.Single(recipes);
        Assert.Equal("r1", recipes[0].Id);
        Assert.Equal("Acta", recipes[0].Name);
        Assert.Equal("Armá un acta", recipes[0].Instruction);
        Assert.True(recipes[0].IsDefault);
    }

    [Fact]
    public void ParseListResponse_Vacio()
    {
        Assert.Empty(AiRecipesClient.ParseListResponse("""{"recipes":[]}"""));
    }

    [Fact]
    public void ParseListResponse_JsonInvalido_DevuelveVacioSinLanzar()
    {
        Assert.Empty(AiRecipesClient.ParseListResponse("no es json"));
    }

    [Fact]
    public void ParseListResponse_ItemSinIdONombre_SeIgnoraElItem()
    {
        var recipes = AiRecipesClient.ParseListResponse(
            """{"recipes":[{"id":"r1","name":"OK","instruction":"x"},{"name":"SinId","instruction":"x"},{"id":"r2","instruction":"x"}]}""");

        Assert.Single(recipes);
        Assert.Equal("r1", recipes[0].Id);
    }

    // ---- BuildApplyRequestBody / BuildErrorMessage -----------------------------------------

    [Fact]
    public void BuildApplyRequestBody_ArmaTranscriptionIdYRecipeId()
    {
        var body = AiRecipesClient.BuildApplyRequestBody("t1", "r1");

        Assert.Equal("""{"transcriptionId":"t1","recipeId":"r1"}""", body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BuildErrorMessage_SesionInvalida_MensajeDeReLogin(HttpStatusCode status)
    {
        Assert.Contains("Iniciá sesión de nuevo", AiRecipesClient.BuildErrorMessage(status, "{}"));
    }

    [Fact]
    public void BuildErrorMessage_Generico_IncluyeStatusYDetalle()
    {
        var msg = AiRecipesClient.BuildErrorMessage(HttpStatusCode.InternalServerError, """{"error":"boom"}""");

        Assert.Contains("500", msg);
        Assert.Contains("boom", msg);
    }

    // ---- ListRecipesAsync -------------------------------------------------------------------

    [Fact]
    public async Task ListRecipesAsync_MandaBearer_ParseaOk()
    {
        var handler = new FakeHandler(_ => Json("""{"recipes":[{"id":"r1","name":"Acta","instruction":"x","isDefault":false}]}"""));
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test/");

        var recipes = await client.ListRecipesAsync("AT", CancellationToken.None);

        Assert.Single(recipes);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("https://backend.test/api/recipes", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task ListRecipesAsync_ErrorHttp_Lanza()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Necesitás iniciar sesión."}""", HttpStatusCode.Unauthorized));
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test");

        await Assert.ThrowsAsync<AiAssistException>(() => client.ListRecipesAsync("AT", CancellationToken.None));
    }

    // ---- ApplyRecipeAsync (streaming) --------------------------------------------------------

    [Fact]
    public async Task ApplyRecipeAsync_ReportaElAcumuladoPorCadaChunk_YDevuelveElTextoCompleto()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(new[] { "Hola ", "mundo", "!" })),
        });
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test");

        var received = new List<string>();
        var progress = new SyncProgress<string>(received.Add);
        var result = await client.ApplyRecipeAsync("t1", "r1", "AT", progress, CancellationToken.None);

        Assert.Equal("Hola mundo!", result);
        Assert.Equal(new[] { "Hola ", "Hola mundo", "Hola mundo!" }, received);
    }

    [Fact]
    public async Task ApplyRecipeAsync_MandaBearerYBodyCorrecto()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(new[] { "ok" })),
        });
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test/");

        await client.ApplyRecipeAsync("t1", "r1", "AT", onProgress: null, CancellationToken.None);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Equal("https://backend.test/api/recipes/apply", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("""{"transcriptionId":"t1","recipeId":"r1"}""", handler.LastBody);
    }

    [Fact]
    public async Task ApplyRecipeAsync_ErrorHttp_LanzaConMensajeAmigable_YNoLlamaAOnProgress()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Llegaste al límite de formatos aplicados por hoy. Probá de nuevo mañana."}""", HttpStatusCode.TooManyRequests));
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test");

        var called = false;
        var progress = new SyncProgress<string>(_ => called = true);

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.ApplyRecipeAsync("t1", "r1", "AT", progress, CancellationToken.None));

        Assert.Equal("Llegaste al límite de formatos aplicados por hoy. Probá de nuevo mañana.", ex.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task ApplyRecipeAsync_SinAccessToken_Lanza()
    {
        var client = new AiRecipesClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.ApplyRecipeAsync("t1", "r1", "", null, CancellationToken.None));
    }

    // ---- ParseRecipeResponse ------------------------------------------------------------------

    [Fact]
    public void ParseRecipeResponse_CasoCompleto()
    {
        var recipe = AiRecipesClient.ParseRecipeResponse(
            """{"recipe":{"id":"r1","name":"Acta","instruction":"Armá un acta","isDefault":true}}""");

        Assert.Equal("r1", recipe.Id);
        Assert.Equal("Acta", recipe.Name);
        Assert.True(recipe.IsDefault);
    }

    [Fact]
    public void ParseRecipeResponse_SinRecipe_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiRecipesClient.ParseRecipeResponse("""{"ok":true}"""));
    }

    [Fact]
    public void ParseRecipeResponse_JsonInvalido_Lanza()
    {
        Assert.Throws<AiAssistException>(() => AiRecipesClient.ParseRecipeResponse("no es json"));
    }

    // ---- BuildSaveRequestBody / BuildSetDefaultRequestBody -------------------------------------

    [Fact]
    public void BuildSaveRequestBody_ArmaNameEInstruction()
    {
        // Texto sin acentos a propósito: JsonSerializer.Serialize escapa no-ASCII por default
        // (\uXXXX) -- comparar contra un literal con tildes rompería el test sin que el código
        // esté mal, mismo motivo que BuildApplyRequestBody de más arriba usa solo IDs ASCII.
        Assert.Equal("""{"name":"Acta","instruction":"Hace un acta"}""",
            AiRecipesClient.BuildSaveRequestBody("Acta", "Hace un acta"));
    }

    [Fact]
    public void BuildSetDefaultRequestBody_ArmaIsDefaultTrue()
    {
        Assert.Equal("""{"isDefault":true}""", AiRecipesClient.BuildSetDefaultRequestBody());
    }

    [Fact]
    public void BuildErrorMessage_BadRequest_UsaDetalleDelServidor()
    {
        var msg = AiRecipesClient.BuildErrorMessage(HttpStatusCode.BadRequest,
            """{"error":"El nombre no puede estar vacío ni superar los 80 caracteres."}""");
        Assert.Equal("El nombre no puede estar vacío ni superar los 80 caracteres.", msg);
    }

    // ---- CreateRecipeAsync ---------------------------------------------------------------------

    [Fact]
    public async Task CreateRecipeAsync_MandaPostConBodyCorrecto_ParseaElFormatoCreado()
    {
        var handler = new FakeHandler(_ => Json("""{"recipe":{"id":"r1","name":"Acta","instruction":"x","isDefault":false}}""", HttpStatusCode.Created));
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test/");

        var recipe = await client.CreateRecipeAsync("Acta", "x", "AT", CancellationToken.None);

        Assert.Equal("r1", recipe.Id);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("AT", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Equal("https://backend.test/api/recipes", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("""{"name":"Acta","instruction":"x"}""", handler.LastBody);
    }

    [Fact]
    public async Task CreateRecipeAsync_ErrorValidacion_LanzaConMensajeDelServidor()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Llegaste al máximo de 30 formatos."}""", HttpStatusCode.BadRequest));
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(() => client.CreateRecipeAsync("Acta", "x", "AT", CancellationToken.None));
        Assert.Equal("Llegaste al máximo de 30 formatos.", ex.Message);
    }

    // ---- UpdateRecipeAsync ---------------------------------------------------------------------

    [Fact]
    public async Task UpdateRecipeAsync_MandaPatchAlIdCorrecto()
    {
        var handler = new FakeHandler(_ => Json("""{"recipe":{"id":"r1","name":"Editado","instruction":"y","isDefault":false}}"""));
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test");

        var recipe = await client.UpdateRecipeAsync("r1", "Editado", "y", "AT", CancellationToken.None);

        Assert.Equal("Editado", recipe.Name);
        Assert.Equal(HttpMethod.Patch, handler.LastRequest!.Method);
        Assert.Equal("https://backend.test/api/recipes/r1", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("""{"name":"Editado","instruction":"y"}""", handler.LastBody);
    }

    // ---- SetDefaultRecipeAsync -------------------------------------------------------------------

    [Fact]
    public async Task SetDefaultRecipeAsync_MandaPatchConIsDefaultTrue()
    {
        var handler = new FakeHandler(_ => Json("""{"recipe":{"id":"r1","name":"Acta","instruction":"x","isDefault":true}}"""));
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test");

        var recipe = await client.SetDefaultRecipeAsync("r1", "AT", CancellationToken.None);

        Assert.True(recipe.IsDefault);
        Assert.Equal(HttpMethod.Patch, handler.LastRequest!.Method);
        Assert.Equal("https://backend.test/api/recipes/r1", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("""{"isDefault":true}""", handler.LastBody);
    }

    // ---- DeleteRecipeAsync -----------------------------------------------------------------------

    [Fact]
    public async Task DeleteRecipeAsync_MandaDeleteAlIdCorrecto()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true}"""));
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test");

        await client.DeleteRecipeAsync("r1", "AT", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("https://backend.test/api/recipes/r1", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task DeleteRecipeAsync_ErrorHttp_Lanza()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError));
        var client = new AiRecipesClient(new HttpClient(handler), "https://backend.test");

        await Assert.ThrowsAsync<AiAssistException>(() => client.DeleteRecipeAsync("r1", "AT", CancellationToken.None));
    }

    [Fact]
    public async Task CreateRecipeAsync_SinAccessToken_Lanza()
    {
        var client = new AiRecipesClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateRecipeAsync("Acta", "x", "", CancellationToken.None));
    }
}
