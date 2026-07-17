using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente del endpoint de resumen con IA del backend (<c>POST {backendBaseUrl}/api/summarize</c>),
/// que devuelve un resumen estructurado (párrafo + puntos clave + tareas) de una transcripción ya
/// sincronizada. Mismo criterio de autenticación (Bearer = access token de la sesión de Supabase)
/// que <see cref="CloudTranscriptionService"/> y <see cref="SyncApiClient"/> -- parte del cambio
/// "Híbrido nativo" (2026-07-13): la app WPF consume el mismo backend que la web y renderiza UI
/// nativa, sin ningún WebView2 de por medio.
/// </summary>
public sealed class AiSummaryClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AiSummaryClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>Arma el cuerpo JSON del POST: <c>{ "id", "force" }</c>. Lógica pura, sin red.</summary>
    public static string BuildRequestBody(string transcriptionId, bool force) =>
        JsonSerializer.Serialize(new SummarizeRequestDto(transcriptionId, force));

    /// <summary>
    /// Parsea la respuesta <c>{ summary, keyPoints, actionItems, cached }</c> de un 200 OK. Lógica
    /// pura (JSON ya leído en memoria). Lanza <see cref="AiAssistException"/> si el JSON es
    /// inválido o no trae "summary" como string no vacío.
    /// </summary>
    public static AiSummaryResult ParseResponse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new AiAssistException("El servidor devolvió una respuesta inválida.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("summary", out var summaryEl) || summaryEl.ValueKind != JsonValueKind.String)
                throw new AiAssistException("El servidor no devolvió el resumen.");

            var summary = summaryEl.GetString() ?? string.Empty;
            var keyPoints = ReadStringArray(root, "keyPoints");
            var actionItems = ReadStringArray(root, "actionItems");
            var cached = root.TryGetProperty("cached", out var cachedEl) && cachedEl.ValueKind == JsonValueKind.True;

            return new AiSummaryResult(summary, keyPoints, actionItems, cached);
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var item in arrEl.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                result.Add(text);
        return result;
    }

    /// <summary>
    /// Mensaje amigable en español según el status HTTP. Mismo criterio que
    /// <see cref="CloudTranscriptionService.BuildErrorMessage"/>: sesión vencida/sin sesión
    /// (401/403), límite diario (429, el backend ya manda el mensaje en español) y el resto como
    /// error genérico con el detalle del cuerpo.
    /// </summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.",
            HttpStatusCode.TooManyRequests => detail.Length > 0 ? detail : "Llegaste al límite diario de resúmenes con IA. Probá mañana.",
            HttpStatusCode.NotFound => "No se encontró la transcripción en el servidor.",
            _ => $"El servidor devolvió {(int)statusCode} {statusCode}: {detail}",
        };
    }

    private static string TryExtractErrorDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? json;
        }
        catch { /* body no era JSON */ }
        return json;
    }

    /// <summary>
    /// Pide el resumen de <paramref name="transcriptionId"/>. <paramref name="accessToken"/> es el
    /// access token de la sesión de Supabase (el mismo que usa el sync, ver
    /// <c>SyncCoordinator.GetValidAccessTokenAsync</c>). <paramref name="force"/> ignora el resumen
    /// cacheado del servidor y vuelve a llamar al LLM ("Regenerar").
    /// </summary>
    public async Task<AiSummaryResult> SummarizeAsync(
        string transcriptionId, bool force, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para resumir.", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/summarize");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(BuildRequestBody(transcriptionId, force), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AiAssistException("No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new AiAssistException(BuildErrorMessage(response.StatusCode, body));
            return ParseResponse(body);
        }
    }

    private sealed record SummarizeRequestDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("force")] bool Force);
}

/// <summary>Resultado parseado de <c>POST /api/summarize</c> (ver <see cref="AiSummaryClient.ParseResponse"/>).</summary>
public sealed record AiSummaryResult(string Summary, IReadOnlyList<string> KeyPoints, IReadOnlyList<string> ActionItems, bool Cached);

/// <summary>
/// Error de una llamada a un endpoint de IA del backend (resumen, formatos, y en el futuro chat).
/// El mensaje ya viene pensado para mostrarse directo en la UI (ver
/// <see cref="AiSummaryClient.BuildErrorMessage"/> / <see cref="AiRecipesClient.BuildErrorMessage"/>).
/// </summary>
public sealed class AiAssistException : Exception
{
    public AiAssistException(string message) : base(message) { }
    public AiAssistException(string message, Exception inner) : base(message, inner) { }
}
