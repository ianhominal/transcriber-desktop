using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente de "Segundo cerebro" (preguntar sobre TODAS las notas del usuario, no una sola
/// transcripción -- ver <c>src/app/api/brain/route.ts</c> del backend web, brief "Híbrido nativo"
/// 2026-07-14). <c>POST {backendBaseUrl}/api/brain</c>, body <c>{ message: UIMessage }</c> con UN
/// SOLO mensaje <c>role: "user"</c> (STATELESS a propósito del lado del servidor: sin historial, ver
/// comentario del route) -- misma forma mínima de <c>UIMessage</c> que <see cref="AiChatClient"/>,
/// solo que acá no hay <c>transcriptionId</c>.
///
/// El wire format de la respuesta es EL MISMO protocolo SSE/UIMessage de <c>toUIMessageStreamResponse()</c>
/// que <see cref="AiChatClient"/> (confirmado leyendo el route: <c>result.toUIMessageStreamResponse(...)</c>,
/// no <c>toTextStreamResponse()</c> como "Formatos"/"Unir notas") -- por eso este cliente REUSA
/// <see cref="AiChatClient.TryParseDataLine"/>/<see cref="AiChatClient.ParseChunk"/> en vez de
/// duplicar el parseo SSE.
/// </summary>
public sealed class AiBrainClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AiBrainClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Arma el cuerpo JSON del POST: <c>{ "message": { "id", "role": "user", "parts": [{ "type":
    /// "text", "text" }] } }</c> -- misma forma mínima de <c>UIMessage</c> que valida
    /// <c>src/app/api/brain/route.ts</c> (rechaza cualquier <c>role</c> que no sea "user"). Lógica
    /// pura, sin red; <paramref name="messageId"/> se recibe como parámetro (no se genera acá) para
    /// que sea determinística y testeable, mismo criterio que <see cref="AiChatClient.BuildRequestBody"/>.
    /// </summary>
    public static string BuildRequestBody(string messageId, string question) =>
        JsonSerializer.Serialize(new BrainRequestDto(
            new BrainMessageDto(messageId, "user", new[] { new BrainMessagePartDto("text", question) })));

    /// <summary>Mismo criterio de mensajes amigables en español que <see cref="AiChatClient.BuildErrorMessage"/>,
    /// adaptado al límite diario propio de "Segundo cerebro" (<c>kind: "brain"</c> en <c>ai_usage_log</c>).</summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.",
            HttpStatusCode.TooManyRequests => detail.Length > 0 ? detail : "Llegaste al límite diario de preguntas del Chat con IA. Probá mañana.",
            HttpStatusCode.BadRequest => detail.Length > 0 ? detail : "Tu pregunta no es válida.",
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
    /// Pregunta <paramref name="question"/> al Segundo cerebro (grounded en las notas más
    /// relevantes del usuario, resuelto server-side). Reporta el texto ACUMULADO por
    /// <paramref name="onProgress"/> a medida que llegan los chunks <c>text-delta</c> del stream SSE
    /// (mismo criterio que <see cref="AiChatClient.SendMessageAsync"/>) y devuelve el texto completo
    /// al terminar. Sin historial: cada pregunta es independiente (ver header comment).
    /// </summary>
    public async Task<string> AskAsync(
        string question, string accessToken, IProgress<string>? onProgress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para usar el Chat con IA.", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Falta la pregunta.", nameof(question));

        var messageId = Guid.NewGuid().ToString();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/brain");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(BuildRequestBody(messageId, question), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sb = new StringBuilder();
            string? streamErrorText = null;

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!AiChatClient.TryParseDataLine(line, out var payload))
                    continue;
                if (payload == "[DONE]")
                    break;

                if (AiChatClient.ParseChunk(payload) is not { } chunk)
                    continue;

                if (chunk.Type == "text-delta" && chunk.Delta is not null)
                {
                    sb.Append(chunk.Delta);
                    onProgress?.Report(sb.ToString());
                }
                else if (chunk.Type == "error")
                {
                    streamErrorText = string.IsNullOrEmpty(chunk.ErrorText)
                        ? "No pudimos generar la respuesta. Probá de nuevo."
                        : chunk.ErrorText;
                    break;
                }
            }

            if (streamErrorText is not null)
                throw new AiAssistException(streamErrorText);

            return sb.ToString();
        }
    }

    private sealed record BrainMessagePartDto(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text);

    private sealed record BrainMessageDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("parts")] IReadOnlyList<BrainMessagePartDto> Parts);

    private sealed record BrainRequestDto(
        [property: JsonPropertyName("message")] BrainMessageDto Message);
}
