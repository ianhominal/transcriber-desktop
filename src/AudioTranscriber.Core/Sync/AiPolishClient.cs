using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente del endpoint de mejora de texto con IA del backend (<c>POST {backendBaseUrl}/api/polish</c>):
/// corrige términos con el vocabulario del usuario y agrega puntuación/párrafos a una transcripción.
/// Pensado sobre todo para el camino Local (Whisper en la PC, ver <see cref="TranscriptionService"/>)
/// que -- a diferencia de la nube -- NO pasa el texto por el corrector de vocabulario al transcribir.
/// El servidor parte internamente los textos largos (hasta 200.000 caracteres) en varios tramos; si
/// alguno no se pudo pulir, <see cref="AiPolishResultDto.PolishedChunks"/> queda por debajo de
/// <see cref="AiPolishResultDto.TotalChunks"/> pero el texto ORIGINAL de ese tramo se conserva (no se
/// pierde contenido). Mismo criterio de autenticación (Bearer = access token de la sesión de Supabase)
/// que <see cref="AiSummaryClient"/>.
/// </summary>
public sealed class AiPolishClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AiPolishClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Arma el cuerpo JSON del POST: <c>{ "text", "transcriptionId" }</c>. <paramref name="transcriptionId"/>
    /// es opcional (null/vacío se manda como string vacío, mismo criterio que <see cref="AiMergeClient.BuildRequestBody"/>
    /// con su "instruction"): el servidor solo guarda el texto pulido si lo recibe con contenido.
    /// Lógica pura, sin red.
    /// </summary>
    public static string BuildRequestBody(string text, string? transcriptionId) =>
        JsonSerializer.Serialize(new PolishRequestDto(text, transcriptionId ?? string.Empty));

    /// <summary>
    /// Parsea la respuesta <c>{ text, polishedChunks, totalChunks }</c> de un 200 OK. Lógica pura
    /// (JSON ya leído en memoria). Lanza <see cref="AiAssistException"/> si el JSON es inválido o no
    /// trae "text" como string.
    /// </summary>
    public static AiPolishResultDto ParseResponse(string json)
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
            if (!root.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                throw new AiAssistException("El servidor no devolvió el texto mejorado.");

            var text = textEl.GetString() ?? string.Empty;
            var polishedChunks = root.TryGetProperty("polishedChunks", out var pcEl) && pcEl.ValueKind == JsonValueKind.Number
                ? pcEl.GetInt32() : 0;
            var totalChunks = root.TryGetProperty("totalChunks", out var tcEl) && tcEl.ValueKind == JsonValueKind.Number
                ? tcEl.GetInt32() : 0;

            return new AiPolishResultDto(text, polishedChunks, totalChunks);
        }
    }

    /// <summary>
    /// A diferencia de <see cref="AiSummaryClient.BuildErrorMessage"/>, acá el servidor YA devuelve
    /// un mensaje humano en español para cualquier status (400/401/429/500) -- se muestra TAL CUAL,
    /// sin reescribirlo con mensajes propios del cliente. Solo hay un respaldo genérico por si el
    /// cuerpo no trae "error" (p.ej. un 502 de un proxy intermedio, que no es JSON).
    /// </summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return detail.Length > 0 ? detail : $"El servidor devolvió {(int)statusCode} {statusCode}.";
    }

    private static string TryExtractErrorDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? string.Empty;
        }
        catch { /* body no era JSON */ }
        return string.Empty;
    }

    /// <summary>
    /// Mejora <paramref name="text"/>: corrige términos con el vocabulario del usuario y agrega
    /// puntuación/párrafos. <paramref name="transcriptionId"/> es opcional -- si se manda, el
    /// servidor además guarda el texto pulido en esa transcripción (ver <see cref="BuildRequestBody"/>).
    /// <paramref name="accessToken"/> es el access token de la sesión de Supabase (el mismo que usa
    /// el sync, ver <c>SyncCoordinator.GetValidAccessTokenAsync</c>).
    /// </summary>
    public async Task<AiPolishResultDto> PolishAsync(
        string text, string? transcriptionId, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para mejorar el texto.", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/polish");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(BuildRequestBody(text, transcriptionId), Encoding.UTF8, "application/json");

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

    private sealed record PolishRequestDto(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("transcriptionId")] string TranscriptionId);
}

/// <summary>Resultado parseado de <c>POST /api/polish</c> (ver <see cref="AiPolishClient.ParseResponse"/>).</summary>
public sealed record AiPolishResultDto(string Text, int PolishedChunks, int TotalChunks);
