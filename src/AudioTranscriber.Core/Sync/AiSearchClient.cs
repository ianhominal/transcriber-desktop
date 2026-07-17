using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente de "Búsqueda" (búsqueda de texto completo sobre TODAS las notas del usuario, ver
/// <c>src/app/api/notes/search/route.ts</c> del backend web -- feature "Segundo cerebro" del brief
/// 2026-07-14). <c>GET {backendBaseUrl}/api/notes/search?q=&lt;query&gt;</c>, respuesta simple (sin
/// streaming) <c>{ results: [{ id, title, createdAt, projectId, snippet }] }</c>. Mismo criterio de
/// autenticación (Bearer) que <see cref="AiSummaryClient"/>.
/// </summary>
public sealed class AiSearchClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AiSearchClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Arma la URL del GET: <c>{baseUrl}/api/notes/search?q={query}</c>, con el query param
    /// URL-encoded (mismo criterio que <c>req.nextUrl.searchParams.get("q")</c> del lado del
    /// servidor). Lógica pura, sin red.
    /// </summary>
    public static string BuildQueryUrl(string baseUrl, string query) =>
        $"{baseUrl.TrimEnd('/')}/api/notes/search?q={Uri.EscapeDataString(query)}";

    /// <summary>
    /// Parsea <c>{ results: [{ id, title, createdAt, projectId, snippet }] }</c>. Lógica pura.
    /// Nunca lanza: un JSON inválido o con forma inesperada devuelve una lista vacía (mismo
    /// criterio best-effort que <see cref="AiRecipesClient.ParseListResponse"/>) -- una búsqueda
    /// sin resultados no es un error, así que no vale la pena distinguir "vacío" de "raro" acá.
    /// </summary>
    public static IReadOnlyList<AiSearchResultDto> ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<AiSearchResultDto>();

            var result = new List<AiSearchResultDto>();
            foreach (var item in arr.EnumerateArray())
            {
                if (TryParseResultItem(item) is { } r)
                    result.Add(r);
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<AiSearchResultDto>();
        }
    }

    private static AiSearchResultDto? TryParseResultItem(JsonElement item)
    {
        var id = GetString(item, "id");
        if (id is null)
            return null; // fila sin id no sirve para abrir la nota -- se ignora, no rompe el resto.

        var title = GetString(item, "title") ?? "Sin título";
        var createdAt = GetString(item, "createdAt") ?? string.Empty;
        var projectId = GetString(item, "projectId");
        var snippet = GetString(item, "snippet") ?? string.Empty;
        return new AiSearchResultDto(id, title, createdAt, projectId, snippet);
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>Mismo criterio de mensajes amigables en español que <see cref="AiSummaryClient.BuildErrorMessage"/>.</summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.",
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
    /// Busca <paramref name="query"/> en todas las notas del usuario logueado. Query vacío/en
    /// blanco devuelve una lista vacía SIN llamar al servidor (mismo resultado que el backend le
    /// daría, ver <c>isValidSearchQuery</c> del route, pero evita el round-trip).
    /// </summary>
    public async Task<IReadOnlyList<AiSearchResultDto>> SearchAsync(string query, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para buscar.", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<AiSearchResultDto>();

        using var req = new HttpRequestMessage(HttpMethod.Get, BuildQueryUrl(_baseUrl, query));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

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
}

/// <summary>Un resultado de <c>GET /api/notes/search</c> (ver <see cref="AiSearchClient.ParseResponse"/>).</summary>
public sealed record AiSearchResultDto(string Id, string Title, string CreatedAt, string? ProjectId, string Snippet);
