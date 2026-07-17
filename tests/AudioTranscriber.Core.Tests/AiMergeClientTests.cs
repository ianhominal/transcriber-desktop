using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class AiMergeClientTests
{
    // Mismo patrón de FakeHandler que AiRecipesClientTests.
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

    // Mismo patrón de ChunkedStream que AiRecipesClientTests (texto plano, sin protocolo).
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

    // ---- CanMergeNoteCount (pura) -------------------------------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void CanMergeNoteCount_RespetaElRango(int count, bool expected)
    {
        Assert.Equal(expected, AiMergeClient.CanMergeNoteCount(count));
    }

    // ---- BuildRequestBody -----------------------------------------------------------------------

    [Fact]
    public void BuildRequestBody_ArmaIdsEInstruccion()
    {
        // Texto sin acentos a propósito: System.Text.Json escapea no-ASCII por default
        // (\uXXXX) -- este test compara el JSON literal, así que evita esa codificación
        // para no acoplarse a un detalle de serialización que no es lo que se está probando.
        var body = AiMergeClient.BuildRequestBody(new[] { "a", "b" }, "Arma una minuta");

        Assert.Equal("""{"transcriptionIds":["a","b"],"instruction":"Arma una minuta"}""", body);
    }

    [Fact]
    public void BuildRequestBody_InstruccionNull_MandaVacia()
    {
        var body = AiMergeClient.BuildRequestBody(new[] { "a", "b" }, null);

        Assert.Equal("""{"transcriptionIds":["a","b"],"instruction":""}""", body);
    }

    // ---- BuildErrorMessage ----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void BuildErrorMessage_SesionInvalida_MensajeDeReLogin(HttpStatusCode status)
    {
        Assert.Contains("Iniciá sesión de nuevo", AiMergeClient.BuildErrorMessage(status, "{}"));
    }

    [Fact]
    public void BuildErrorMessage_NotFound_UsaDetalleDelServidor()
    {
        var msg = AiMergeClient.BuildErrorMessage(HttpStatusCode.NotFound,
            """{"error":"No pudimos encontrar alguna de las notas elegidas."}""");
        Assert.Equal("No pudimos encontrar alguna de las notas elegidas.", msg);
    }

    // ---- Headers (puros) -----------------------------------------------------------------------

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void ParseTruncatedHeader_ParseaBooleano(string? value, bool expected)
    {
        Assert.Equal(expected, AiMergeClient.ParseTruncatedHeader(value));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData(null, 0)]
    [InlineData("no-es-numero", 0)]
    public void ParseIncludedCountHeader_ParseaEntero(string? value, int expected)
    {
        Assert.Equal(expected, AiMergeClient.ParseIncludedCountHeader(value));
    }

    // ---- MergeNotesAsync (streaming end-to-end) -----------------------------------------------

    [Fact]
    public async Task MergeNotesAsync_ReportaElAcumuladoYDevuelveHeaders()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedStream(new[] { "Documento ", "unido completo" })),
            };
            response.Headers.Add("X-Merge-Truncated", "true");
            response.Headers.Add("X-Merge-Included-Count", "3");
            return response;
        });
        var client = new AiMergeClient(new HttpClient(handler), "https://backend.test");

        var received = new List<string>();
        var progress = new SyncProgress<string>(received.Add);
        var result = await client.MergeNotesAsync(new[] { "a", "b" }, "instrucción", "AT", progress, CancellationToken.None);

        Assert.Equal("Documento unido completo", result.Text);
        Assert.True(result.Truncated);
        Assert.Equal(3, result.IncludedCount);
        Assert.Equal(new[] { "Documento ", "Documento unido completo" }, received);
    }

    [Fact]
    public async Task MergeNotesAsync_SinHeaders_DevuelveDefaults()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(new[] { "texto" })),
        });
        var client = new AiMergeClient(new HttpClient(handler), "https://backend.test");

        var result = await client.MergeNotesAsync(new[] { "a", "b" }, null, "AT", null, CancellationToken.None);

        Assert.False(result.Truncated);
        Assert.Equal(0, result.IncludedCount);
    }

    [Fact]
    public async Task MergeNotesAsync_MandaBearerUrlYBodyCorrectos()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(new[] { "x" })),
        });
        var client = new AiMergeClient(new HttpClient(handler), "https://backend.test/");

        await client.MergeNotesAsync(new[] { "a", "b" }, "inst", "AT", null, CancellationToken.None);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("https://backend.test/api/notes/merge", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"transcriptionIds\":[\"a\",\"b\"]", handler.LastBody);
    }

    [Fact]
    public async Task MergeNotesAsync_ErrorHttpPrevio_LanzaConMensajeAmigable()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"No pudimos encontrar alguna de las notas elegidas."}""", HttpStatusCode.NotFound));
        var client = new AiMergeClient(new HttpClient(handler), "https://backend.test");

        var ex = await Assert.ThrowsAsync<AiAssistException>(
            () => client.MergeNotesAsync(new[] { "a", "b" }, null, "AT", null, CancellationToken.None));

        Assert.Equal("No pudimos encontrar alguna de las notas elegidas.", ex.Message);
    }

    [Fact]
    public async Task MergeNotesAsync_SinAccessToken_Lanza()
    {
        var client = new AiMergeClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.MergeNotesAsync(new[] { "a", "b" }, null, "", null, CancellationToken.None));
    }

    [Fact]
    public async Task MergeNotesAsync_CantidadInvalida_Lanza()
    {
        var client = new AiMergeClient(new HttpClient(new FakeHandler(_ => Json("{}"))), "https://backend.test");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.MergeNotesAsync(new[] { "a" }, null, "AT", null, CancellationToken.None));
    }
}
