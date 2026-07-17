using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente de "Guardar como nota" (<c>POST {backendBaseUrl}/api/notes</c>, ver
/// <c>src/app/api/notes/route.ts</c> del backend web): crea una transcripción TEXT-ONLY nueva
/// (sin audio) a partir de texto arbitrario -- hoy usado para guardar una respuesta del chat con IA
/// (<see cref="AiChatClient"/>) como nota independiente. Body <c>{ "text" }</c>; el server deriva el
/// título, recorta a 40.000 caracteres y tagea la nota como "chat" (ver <c>buildChatNoteDraft</c>),
/// así que este cliente no necesita replicar esa lógica -- solo manda el texto crudo y parsea
/// <c>{ id, title }</c>. Mismo criterio de autenticación que <see cref="AiSummaryClient"/>.
/// </summary>
public sealed class AiNotesClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AiNotesClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>Arma el cuerpo JSON del POST: <c>{ "text" }</c>. Lógica pura, sin red.</summary>
    public static string BuildCreateRequestBody(string text) =>
        JsonSerializer.Serialize(new CreateNoteRequestDto(text));

    /// <summary>
    /// Parsea la respuesta <c>{ id, title }</c> de un 200 OK. Lógica pura. Lanza
    /// <see cref="AiAssistException"/> si el JSON es inválido o no trae "id" como string no vacío.
    /// </summary>
    public static AiNoteCreatedDto ParseCreateResponse(string json)
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
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                throw new AiAssistException("El servidor no devolvió la nota guardada.");

            var id = idEl.GetString() ?? string.Empty;
            var title = root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                ? titleEl.GetString() ?? string.Empty
                : string.Empty;

            return new AiNoteCreatedDto(id, title);
        }
    }

    /// <summary>Mismo criterio de mensajes amigables en español que <see cref="AiSummaryClient.BuildErrorMessage"/>,
    /// adaptado a los errores propios de <c>/api/notes</c> (límite diario COMPARTIDO con transcripciones,
    /// ver comentario del route).</summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.",
            HttpStatusCode.TooManyRequests => detail.Length > 0 ? detail : "Llegaste al límite diario de transcripciones. Probá mañana.",
            HttpStatusCode.BadRequest => detail.Length > 0 ? detail : "No hay contenido para guardar.",
            HttpStatusCode.ServiceUnavailable => detail.Length > 0 ? detail : "No pudimos verificar tu límite diario. Probá de nuevo.",
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

    /// <summary>Guarda <paramref name="text"/> como una nota nueva. <paramref name="accessToken"/> es
    /// el access token de la sesión de Supabase (mismo criterio que el resto de los clientes de IA).</summary>
    public async Task<AiNoteCreatedDto> CreateNoteAsync(string text, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para guardar la nota.", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/notes");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(BuildCreateRequestBody(text), Encoding.UTF8, "application/json");

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
            return ParseCreateResponse(body);
        }
    }

    private sealed record CreateNoteRequestDto([property: JsonPropertyName("text")] string Text);
}

/// <summary>Resultado parseado de <c>POST /api/notes</c> (ver <see cref="AiNotesClient.ParseCreateResponse"/>).</summary>
public sealed record AiNoteCreatedDto(string Id, string Title);
