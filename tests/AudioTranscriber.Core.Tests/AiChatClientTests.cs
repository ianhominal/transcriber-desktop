using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AiChatClientTests
{
    // Mismo patrón de FakeHandler que AiRecipesClientTests/SyncApiClientTests.
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
    /// mismo patrón que AiRecipesClientTests.ChunkedStream, acá con el wire format REAL del stream
    /// SSE de <c>toUIMessageStreamResponse()</c> (confirmado leyendo <c>node_modules/ai</c> del
    /// backend web) partido en pedazos arbitrarios, incluso a mitad de línea, para probar que
    /// <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> reensambla bien across chunks.
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

    /// <summary><see cref="IProgress{T}"/> sincrónico para test -- mismo motivo que
    /// AiRecipesClientTests.SyncProgress (evita la carrera de <see cref="Progress{T}"/> real, que
    /// siempre marshalea vía SynchronizationContext.Post).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        public SyncProgress(Action<T> callback) => _callback = callback;
        public void Report(T value) => _callback(value);
    }

    /// <summary>Wire format real de <c>toUIMessageStreamResponse()</c> para una respuesta simple de
    /// texto (sin tools), armado como UN SOLO string -- los tests lo parten en pedazos distintos
    /// para simular streaming real.</summary>
    private const string FullSseResponse =
        "data: {\"type\":\"start\",\"messageId\":\"m1\"}\n\n" +
        "data: {\"type\":\"start-step\"}\n\n" +
        "data: {\"type\":\"text-start\",\"id\":\"text-1\"}\n\n" +
        "data: {\"type\":\"text-delta\",\"id\":\"text-1\",\"delta\":\"Hola\"}\n\n" +
        "data: {\"type\":\"text-delta\",\"id\":\"text-1\",\"delta\":\" mundo\"}\n\n" +
        "data: {\"type\":\"text-delta\",\"id\":\"text-1\",\"delta\":\"!\"}\n\n" +
        "data: {\"type\":\"text-end\",\"id\":\"text-1\"}\n\n" +
        "data: {\"type\":\"finish-step\"}\n\n" +
        "data: {\"type\":\"finish\"}\n\n" +
        "data: [DONE]\n\n";

    // ---- TryParseDataLine (pura) ------------------------------------------------------------

    [Fact]
    public void TryParseDataLine_LineaDeDatos_DevuelveElPayload()
    {
        var ok = AiChatClient.TryParseDataLine("""data: {"type":"text-delta","id":"text-1","delta":"Hola"}""", out var payload);

        Assert.True(ok);
        Assert.Equal("""{"type":"text-delta","id":"text-1","delta":"Hola"}""", payload);
    }

    [Fact]
    public void TryParseDataLine_LineaDeDatosDone_DevuelveElLiteral()
    {
        var ok = AiChatClient.TryParseDataLine("data: [DONE]", out var payload);

        Assert.True(ok);
        Assert.Equal("[DONE]", payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":comment")]
    [InlineData("event: message")]
    public void TryParseDataLine_LineaSinPrefijoData_DevuelveFalse(string line)
    {
        Assert.False(AiChatClient.TryParseDataLine(line, out _));
    }

    // ---- ParseChunk (pura) --------------------------------------------------------------------

    [Fact]
    public void ParseChunk_TextDelta_ExtraeIdYDelta()
    {
        var chunk = AiChatClient.ParseChunk("""{"type":"text-delta","id":"text-1","delta":"Hola"}""");

        Assert.NotNull(chunk);
        Assert.Equal("text-delta", chunk!.Value.Type);
        Assert.Equal("Hola", chunk.Value.Delta);
    }

    [Fact]
    public void ParseChunk_Error_ExtraeErrorText()
    {
        var chunk = AiChatClient.ParseChunk("""{"type":"error","errorText":"No pudimos generar la respuesta. Probá de nuevo."}""");

        Assert.NotNull(chunk);
        Assert.Equal("error", chunk!.Value.Type);
        Assert.Equal("No pudimos generar la respuesta. Probá de nuevo.", chunk.Value.ErrorText);
    }

    [Theory]
    [InlineData("""{"type":"start","messageId":"m1"}""")]
    [InlineData("""{"type":"start-step"}""")]
    [InlineData("""{"type":"text-start","id":"text-1"}""")]
    [InlineData("""{"type":"text-end","id":"text-1"}""")]
    [InlineData("""{"type":"finish-step"}""")]
    [InlineData("""{"type":"finish","finishReason":"stop"}""")]
    public void ParseChunk_TiposIgnorados_DevuelveChunkSinDeltaNiError(string json)
    {
        var chunk = AiChatClient.ParseChunk(json);

        Assert.NotNull(chunk);
        Assert.Null(chunk!.Value.Delta);
        Assert.Null(chunk.Value.ErrorText);
    }

    [Fact]
    public void ParseChunk_JsonInvalido_DevuelveNullSinLanzar()
    {
        Assert.Null(AiChatClient.ParseChunk("no es json"));
    }

    [Fact]
    public void ParseChunk_SinType_DevuelveNull()
    {
        Assert.Null(AiChatClient.ParseChunk("""{"foo":"bar"}"""));
    }

    // ---- BuildRequestBody -----------------------------------------------------------------------

    [Fact]
    public void BuildRequestBody_ArmaUIMessageConRoleUser()
    {
        var body = AiChatClient.BuildRequestBody("t1", "m1", "Hola");

        Assert.Equal(
            """{"transcriptionId":"t1","message":{"id":"m1","role":"user","parts":[{"type":"text","text":"Hola"}]}}""",
            body);
    }

    // ---- BuildErrorMessage ----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BuildErrorMessage_SesionInvalida_MensajeDeReLogin(HttpStatusCode status)
    {
        Assert.Contains("Iniciá sesión de nuevo", AiChatClient.BuildErrorMessage(status, "{}"));
    }

    [Fact]
    public void BuildErrorMessage_LimiteDiario_UsaDetalleDelServidor()
    {
        var msg = AiChatClient.BuildErrorMessage(HttpStatusCode.TooManyRequests,
            """{"error":"Llegaste al límite diario de mensajes de chat. Probá mañana."}""");
        Assert.Equal("Llegaste al límite diario de mensajes de chat. Probá mañana.", msg);
    }

    // ---- SendMessageAsync (streaming end-to-end sobre el wire format real) --------------------

    [Fact]
    public async Task SendMessageAsync_ParseaElStreamSse_ReportaElAcumuladoYDevuelveElTextoCompleto()
    {
        // Partido en pedazos que NO respetan los límites de línea (mitad de un "data: {...}" en un
        // chunk, el resto en el siguiente) -- prueba que el lector reensambla líneas across reads.
        var wireChunks = new[]
        {
            FullSseResponse[..40],
            FullSseResponse[40..120],
            FullSseResponse[120..],
        };
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(wireChunks)),
        });
        var client = new AiChatClient(new HttpClient(handler), "https://backend.test");

        var received = new List<string>();
        var progress = new SyncProgress<string>(received.Add);
        var result = await client.SendMessageAsync("t1", "Hola", "AT", progress, CancellationToken.None);

        Assert.Equal("Hola mundo!", result);
        Assert.Equal(new[] { "Hola", "Hola mundo", "Hola mundo!" }, received);
    }

    [Fact]
    public async Task SendMessageAsync_MandaBearerYBodyCorrecto()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(new[] { "data: [DONE]\n\n" })),
        });
        var client = new AiChatClient(new HttpClient(handler), "https://backend.test/");

        await client.SendMessageAsync("t1", "Hola", "AT", onProgress: null, CancellationToken.None);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("https://backend.test/api/chat", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"transcriptionId\":\"t1\"", handler.LastBody);
        Assert.Contains("\"text\":\"Hola\"", handler.LastBody);
    }

    [Fact]
    public async Task SendMessageAsync_ErrorHttpPrevio_LanzaConMensajeAmigable_YNoLlamaAOnProgress()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Llegaste al límite diario de mensajes de chat. Probá mañana."}""", HttpStatusCode.TooManyRequests));
        var client = new AiChatClient(new HttpClient(handler), "https://backend.test");

        var called = false;
        var progress = new SyncProgress<string>(_ => called = true);

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.SendMessageAsync("t1", "Hola", "AT", progress, CancellationToken.None));

        Assert.Equal("Llegaste al límite diario de mensajes de chat. Probá mañana.", ex.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task SendMessageAsync_ErrorChunkAMitadDeStream_LanzaConElErrorTextDelChunk()
    {
        const string sse =
            "data: {\"type\":\"start\",\"messageId\":\"m1\"}\n\n" +
            "data: {\"type\":\"error\",\"errorText\":\"No pudimos generar la respuesta. Probá de nuevo.\"}\n\n" +
            "data: {\"type\":\"finish\"}\n\n" +
            "data: [DONE]\n\n";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(new[] { sse })),
        });
        var client = new AiChatClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.SendMessageAsync("t1", "Hola", "AT", null, CancellationToken.None));

        Assert.Equal("No pudimos generar la respuesta. Probá de nuevo.", ex.Message);
    }

    [Fact]
    public async Task SendMessageAsync_SinAccessToken_Lanza()
    {
        var client = new AiChatClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendMessageAsync("t1", "Hola", "", null, CancellationToken.None));
    }

    [Fact]
    public async Task SendMessageAsync_MensajeVacio_Lanza()
    {
        var client = new AiChatClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendMessageAsync("t1", "   ", "AT", null, CancellationToken.None));
    }
}
