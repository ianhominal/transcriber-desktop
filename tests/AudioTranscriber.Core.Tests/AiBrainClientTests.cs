using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AiBrainClientTests
{
    // Mismo patrón de FakeHandler que AiChatClientTests.
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

    // Mismo patrón de ChunkedStream que AiChatClientTests -- el wire format es el mismo protocolo.
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

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _callback;
        public SyncProgress(Action<T> callback) => _callback = callback;
        public void Report(T value) => _callback(value);
    }

    private const string FullSseResponse =
        "data: {\"type\":\"start\",\"messageId\":\"m1\"}\n\n" +
        "data: {\"type\":\"text-delta\",\"id\":\"text-1\",\"delta\":\"Dijiste\"}\n\n" +
        "data: {\"type\":\"text-delta\",\"id\":\"text-1\",\"delta\":\" X ayer\"}\n\n" +
        "data: {\"type\":\"finish\"}\n\n" +
        "data: [DONE]\n\n";

    // ---- BuildRequestBody -----------------------------------------------------------------------

    [Fact]
    public void BuildRequestBody_ArmaUIMessageSinTranscriptionId()
    {
        // Texto sin acentos/signos no-ASCII a propósito: System.Text.Json escapea no-ASCII por
        // default (\uXXXX) -- este test compara el JSON literal, así que evita esa codificación
        // para no acoplarse a un detalle de serialización que no es lo que se está probando.
        var body = AiBrainClient.BuildRequestBody("m1", "Que dije sobre X?");

        Assert.Equal(
            """{"message":{"id":"m1","role":"user","parts":[{"type":"text","text":"Que dije sobre X?"}]}}""",
            body);
    }

    // ---- BuildErrorMessage ----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BuildErrorMessage_SesionInvalida_MensajeDeReLogin(HttpStatusCode status)
    {
        Assert.Contains("Iniciá sesión de nuevo", AiBrainClient.BuildErrorMessage(status, "{}"));
    }

    [Fact]
    public void BuildErrorMessage_LimiteDiario_UsaDetalleDelServidor()
    {
        var msg = AiBrainClient.BuildErrorMessage(HttpStatusCode.TooManyRequests,
            """{"error":"Llegaste al límite diario de preguntas al Segundo cerebro. Probá mañana."}""");
        Assert.Equal("Llegaste al límite diario de preguntas al Segundo cerebro. Probá mañana.", msg);
    }

    [Fact]
    public void BuildErrorMessage_LimiteDiario_SinDetalle_UsaMensajeDefault()
    {
        var msg = AiBrainClient.BuildErrorMessage(HttpStatusCode.TooManyRequests, "");
        Assert.Equal("Llegaste al límite diario de preguntas del Chat con IA. Probá mañana.", msg);
    }

    // ---- AskAsync (streaming end-to-end sobre el wire format real) ----------------------------

    [Fact]
    public async Task AskAsync_ParseaElStreamSse_ReportaElAcumuladoYDevuelveElTextoCompleto()
    {
        var wireChunks = new[]
        {
            FullSseResponse[..30],
            FullSseResponse[30..90],
            FullSseResponse[90..],
        };
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(wireChunks)),
        });
        var client = new AiBrainClient(new HttpClient(handler), "https://backend.test");

        var received = new List<string>();
        var progress = new SyncProgress<string>(received.Add);
        var result = await client.AskAsync("¿Qué dije sobre X?", "AT", progress, CancellationToken.None);

        Assert.Equal("Dijiste X ayer", result);
        Assert.Equal(new[] { "Dijiste", "Dijiste X ayer" }, received);
    }

    [Fact]
    public async Task AskAsync_MandaBearerYUrlYBodyCorrectos()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(new[] { "data: [DONE]\n\n" })),
        });
        var client = new AiBrainClient(new HttpClient(handler), "https://backend.test/");

        await client.AskAsync("Que dije sobre X?", "AT", onProgress: null, CancellationToken.None);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("https://backend.test/api/brain", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"role\":\"user\"", handler.LastBody);
        Assert.Contains("\"text\":\"Que dije sobre X?\"", handler.LastBody);
        Assert.DoesNotContain("transcriptionId", handler.LastBody);
    }

    [Fact]
    public async Task AskAsync_ErrorHttpPrevio_LanzaConMensajeAmigable()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Llegaste al límite diario de preguntas al Segundo cerebro. Probá mañana."}""", HttpStatusCode.TooManyRequests));
        var client = new AiBrainClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.AskAsync("X", "AT", null, CancellationToken.None));

        Assert.Equal("Llegaste al límite diario de preguntas al Segundo cerebro. Probá mañana.", ex.Message);
    }

    [Fact]
    public async Task AskAsync_ErrorChunkAMitadDeStream_LanzaConElErrorTextDelChunk()
    {
        const string sse =
            "data: {\"type\":\"start\",\"messageId\":\"m1\"}\n\n" +
            "data: {\"type\":\"error\",\"errorText\":\"No pudimos generar la respuesta. Probá de nuevo.\"}\n\n" +
            "data: [DONE]\n\n";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(new[] { sse })),
        });
        var client = new AiBrainClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.AskAsync("X", "AT", null, CancellationToken.None));

        Assert.Equal("No pudimos generar la respuesta. Probá de nuevo.", ex.Message);
    }

    [Fact]
    public async Task AskAsync_SinAccessToken_Lanza()
    {
        var client = new AiBrainClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.AskAsync("X", "", null, CancellationToken.None));
    }

    [Fact]
    public async Task AskAsync_PreguntaVacia_Lanza()
    {
        var client = new AiBrainClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.AskAsync("   ", "AT", null, CancellationToken.None));
    }
}
