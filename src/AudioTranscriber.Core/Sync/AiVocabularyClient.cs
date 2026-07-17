using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente de "Vocabulario" (diccionario custom de nombres/jerga que el usuario siempre corrige a
/// mano, ver <c>src/lib/vocabulary/*</c> del backend web): lista los términos guardados
/// (<c>GET /api/vocabulary</c>), agrega uno nuevo (<c>POST /api/vocabulary</c>), edita
/// (<c>PATCH /api/vocabulary/[id]</c>) y borra (<c>DELETE /api/vocabulary/[id]</c>). Mismo patrón de
/// autenticación (Bearer = access token de la sesión de Supabase) y de mensajes de error que
/// <see cref="AiRecipesClient"/> -- REPLICADO a propósito (ver comentario de esa clase), no reusado
/// por herencia: cada cliente tiene su propio shape de body/respuesta y su propio texto de error.
/// </summary>
public sealed class AiVocabularyClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AiVocabularyClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Parsea <c>{ terms: [{ id, term, createdAt }] }</c>. Lógica pura. Nunca lanza: un JSON
    /// inválido o con forma inesperada devuelve una lista vacía (mismo criterio best-effort que
    /// <see cref="AiRecipesClient.ParseListResponse"/>).
    /// </summary>
    public static IReadOnlyList<AiVocabularyTermDto> ParseListResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("terms", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<AiVocabularyTermDto>();

            var result = new List<AiVocabularyTermDto>();
            foreach (var item in arr.EnumerateArray())
            {
                if (TryParseTermItem(item) is { } term)
                    result.Add(term);
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<AiVocabularyTermDto>();
        }
    }

    /// <summary>
    /// Parsea la respuesta de un solo término: <c>{ term: { id, term, createdAt } }</c> -- forma que
    /// devuelven <c>POST /api/vocabulary</c> (agregar) y <c>PATCH /api/vocabulary/[id]</c> (editar).
    /// Lanza <see cref="AiAssistException"/> si el JSON es inválido o no trae un "term" con forma
    /// mínima utilizable (misma razón que <see cref="AiRecipesClient.ParseRecipeResponse"/>: acá SÍ
    /// importa fallar fuerte, es la confirmación de una escritura).
    /// </summary>
    public static AiVocabularyTermDto ParseTermResponse(string json)
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
            if (!doc.RootElement.TryGetProperty("term", out var item) || TryParseTermItem(item) is not { } term)
                throw new AiAssistException("El servidor no devolvió el término guardado.");
            return term;
        }
    }

    private static AiVocabularyTermDto? TryParseTermItem(JsonElement item)
    {
        var id = GetString(item, "id");
        var term = GetString(item, "term");
        if (id is null || term is null)
            return null; // fila sin forma mínima utilizable: se ignora, no rompe el resto de la lista.

        var createdAt = GetString(item, "createdAt") ?? string.Empty;
        return new AiVocabularyTermDto(id, term, createdAt);
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>Arma el cuerpo JSON de agregar/editar: <c>{ "term" }</c> (mismo shape para <c>POST /api/vocabulary</c> y <c>PATCH /api/vocabulary/[id]</c>).</summary>
    public static string BuildSaveRequestBody(string term) => JsonSerializer.Serialize(new SaveTermRequestDto(term));

    /// <summary>Mismo criterio de mensajes amigables en español que <see cref="AiRecipesClient.BuildErrorMessage"/>.</summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.",
            // 409: término duplicado (mismo texto exacto que ya manda el backend, ver
            // src/lib/vocabulary/store.ts -- isDuplicateTermError).
            HttpStatusCode.Conflict => detail.Length > 0 ? detail : "Ese término ya está en tu vocabulario.",
            // 400: validación (término vacío/de más de 80 caracteres) o tope de 100 términos
            // alcanzado -- el backend ya manda el mensaje en español listo para mostrar.
            HttpStatusCode.BadRequest => detail.Length > 0 ? detail : "El término no es válido.",
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

    /// <summary>Lista el vocabulario custom del usuario logueado, en orden de carga (más viejo primero).</summary>
    public async Task<IReadOnlyList<AiVocabularyTermDto>> ListTermsAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para listar el vocabulario.", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/vocabulary");
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
            return ParseListResponse(body);
        }
    }

    /// <summary>Agrega un término nuevo. Body <c>{ term }</c>, respuesta 201 <c>{ term }</c>.</summary>
    public async Task<AiVocabularyTermDto> AddTermAsync(string term, string accessToken, CancellationToken ct) =>
        await SendSaveRequestAsync(HttpMethod.Post, $"{_baseUrl}/api/vocabulary", term, accessToken, ct);

    /// <summary>Edita el texto de un término existente. Body <c>{ term }</c>, respuesta <c>{ term }</c>.</summary>
    public async Task<AiVocabularyTermDto> UpdateTermAsync(string termId, string term, string accessToken, CancellationToken ct) =>
        await SendSaveRequestAsync(HttpMethod.Patch, $"{_baseUrl}/api/vocabulary/{termId}", term, accessToken, ct);

    /// <summary>POST/PATCH compartidos por agregar y editar: mismo shape de respuesta <c>{ term }</c> en los dos.</summary>
    private async Task<AiVocabularyTermDto> SendSaveRequestAsync(HttpMethod method, string url, string term, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para guardar el término.", nameof(accessToken));

        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(BuildSaveRequestBody(term), Encoding.UTF8, "application/json");

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
            return ParseTermResponse(body);
        }
    }

    /// <summary>Borra un término. Requiere sesión y ownership (scopeado a <c>userId</c> además de RLS, del lado del backend).</summary>
    public async Task DeleteTermAsync(string termId, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para borrar el término.", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/vocabulary/{termId}");
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
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                throw new AiAssistException(BuildErrorMessage(response.StatusCode, errorBody));
            }
        }
    }

    private sealed record SaveTermRequestDto([property: JsonPropertyName("term")] string Term);
}

/// <summary>Un término guardado por el usuario (ver <c>src/lib/vocabulary/types.ts</c> del backend web).</summary>
public sealed record AiVocabularyTermDto(string Id, string Term, string CreatedAt);
