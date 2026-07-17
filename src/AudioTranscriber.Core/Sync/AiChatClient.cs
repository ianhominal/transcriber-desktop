using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente de "Chat con IA" sobre una transcripción (<c>POST {backendBaseUrl}/api/chat</c>). Body
/// <c>{ transcriptionId, message }</c> -- el cliente manda SOLO el último mensaje del usuario (el
/// server reconstruye el historial leyendo <c>chat_messages</c>, ver <c>src/app/api/chat/route.ts</c>
/// del backend web), mismo criterio "sending only the last message" documentado por el AI SDK.
///
/// A diferencia de <see cref="AiRecipesClient.ApplyRecipeAsync"/> (streaming de TEXTO PLANO, sin
/// protocolo), este endpoint responde con <c>result.toUIMessageStreamResponse()</c> del AI SDK v6 --
/// wire format confirmado leyendo <c>node_modules/ai/dist/index.js</c> del backend web
/// (<c>JsonToSseTransformStream</c> + <c>ui-message-chunks.ts</c>):
/// <code>
/// Content-Type: text/event-stream
/// x-vercel-ai-ui-message-stream: v1
///
/// data: {"type":"start","messageId":"..."}
///
/// data: {"type":"start-step"}
///
/// data: {"type":"text-start","id":"text-1"}
///
/// data: {"type":"text-delta","id":"text-1","delta":"Hola"}
///
/// data: {"type":"text-delta","id":"text-1","delta":" mundo"}
///
/// data: {"type":"text-end","id":"text-1"}
///
/// data: {"type":"finish-step"}
///
/// data: {"type":"finish"}
///
/// data: [DONE]
///
/// </code>
/// Cada evento SSE es EXACTAMENTE una línea <c>data: {json de una sola línea}</c> seguida de una
/// línea en blanco (<c>JsonToSseTransformStream</c> hace <c>JSON.stringify</c> sin pretty-print, así
/// que el JSON nunca trae saltos de línea propios) -- por eso alcanza con leer línea por línea
/// (<see cref="StreamReader.ReadLineAsync(CancellationToken)"/>) sin tener que acumular bloques SSE
/// multilínea. Este cliente solo le presta atención a dos tipos de chunk: <c>text-delta</c> (el
/// texto de la respuesta, que se va acumulando y reportando EN VIVO por <see cref="IProgress{T}"/>,
/// mismo criterio que <see cref="AiRecipesClient.ApplyRecipeAsync"/>) y <c>error</c> (falla del
/// modelo a mitad de stream, ver <c>onError</c> del route) -- el resto (<c>start</c>,
/// <c>start-step</c>, <c>text-start</c>, <c>text-end</c>, <c>finish-step</c>, <c>finish</c>,
/// <c>message-metadata</c>, y cualquier chunk de tool/reasoning) se ignora: el chat MVP no usa tools
/// ni adjuntos, solo un único part de texto por respuesta.
/// </summary>
public sealed class AiChatClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AiChatClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Arma el cuerpo JSON del POST: <c>{ "transcriptionId", "message": { "id", "role": "user",
    /// "parts": [{ "type": "text", "text" }] } }</c> -- forma exacta de <c>UIMessage</c> que valida
    /// <c>src/app/api/chat/route.ts</c> (rechaza cualquier otro <c>role</c>). Lógica pura, sin red;
    /// <paramref name="messageId"/> se recibe como parámetro (no se genera acá) para que sea
    /// determinística y testeable -- ver <see cref="SendMessageAsync"/> para el <c>Guid</c> real.
    /// </summary>
    public static string BuildRequestBody(string transcriptionId, string messageId, string userText) =>
        JsonSerializer.Serialize(new ChatRequestDto(
            transcriptionId,
            new ChatMessageDto(messageId, "user", new[] { new ChatMessagePartDto("text", userText) })));

    /// <summary>Un chunk ya tipado del stream SSE (ver comentario de clase). <see cref="Delta"/> solo
    /// se completa para <c>text-delta</c>; <see cref="ErrorText"/> solo para <c>error</c>.</summary>
    public readonly record struct ChatSseChunk(string Type, string? Delta, string? ErrorText);

    /// <summary>
    /// true si <paramref name="line"/> es una línea de datos SSE (<c>"data: ..."</c>); en ese caso
    /// <paramref name="payload"/> es el contenido después del prefijo -- el JSON del chunk, o el
    /// literal <c>[DONE]</c> que <c>JsonToSseTransformStream</c> manda al cerrar el stream (ver
    /// <c>flush()</c> en <c>json-to-sse-transform-stream.ts</c>). Cualquier otra línea (blanco entre
    /// eventos, comentarios SSE con <c>:</c>, etc.) devuelve false. Lógica pura.
    /// </summary>
    public static bool TryParseDataLine(string line, out string payload)
    {
        const string prefix = "data: ";
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            payload = line[prefix.Length..];
            return true;
        }
        payload = string.Empty;
        return false;
    }

    /// <summary>
    /// Parsea el JSON de una línea de datos ya extraída por <see cref="TryParseDataLine"/> (nunca se
    /// le pasa el literal <c>[DONE]</c>, ese caso se maneja antes) al chunk tipado que le importa a
    /// este cliente. JSON inválido, o sin un "type" string, devuelve null -- se ignora ese chunk en
    /// vez de tirar abajo todo el chat por un evento con forma inesperada (mismo criterio
    /// best-effort que <see cref="AiRecipesClient.ParseListResponse"/>). Lógica pura.
    /// </summary>
    public static ChatSseChunk? ParseChunk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return null;

            var type = typeEl.GetString()!;
            var delta = root.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String
                ? deltaEl.GetString()
                : null;
            var errorText = root.TryGetProperty("errorText", out var errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : null;

            return new ChatSseChunk(type, delta, errorText);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Mismo criterio de mensajes amigables en español que <see cref="AiSummaryClient.BuildErrorMessage"/>,
    /// adaptado a los errores propios de <c>/api/chat</c> (límite diario de MENSAJES, no de resúmenes/formatos).</summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.",
            HttpStatusCode.TooManyRequests => detail.Length > 0 ? detail : "Llegaste al límite diario de mensajes de chat. Probá mañana.",
            HttpStatusCode.NotFound => detail.Length > 0 ? detail : "No se encontró la transcripción en el servidor.",
            HttpStatusCode.BadRequest => detail.Length > 0 ? detail : "Tu mensaje no es válido.",
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
    /// Manda <paramref name="userText"/> como mensaje nuevo del chat sobre <paramref name="transcriptionId"/>.
    /// Reporta el texto ACUMULADO de la respuesta por <paramref name="onProgress"/> a medida que llegan
    /// los chunks <c>text-delta</c> (mismo criterio que <see cref="AiRecipesClient.ApplyRecipeAsync"/>:
    /// el caller solo pinta el valor recibido, sin mantener su propio acumulador) y devuelve el texto
    /// completo al terminar. Si el server manda un chunk <c>error</c> a mitad de stream, corta la
    /// lectura y lanza <see cref="AiAssistException"/> con ese mensaje (el texto parcial ya reportado
    /// por <paramref name="onProgress"/> se descarta -- mismo criterio "fail fast" que un error HTTP).
    /// </summary>
    public async Task<string> SendMessageAsync(
        string transcriptionId, string userText, string accessToken,
        IProgress<string>? onProgress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para chatear.", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("Falta el mensaje.", nameof(userText));

        var messageId = Guid.NewGuid().ToString();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(BuildRequestBody(transcriptionId, messageId, userText), Encoding.UTF8, "application/json");

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
                if (!TryParseDataLine(line, out var payload))
                    continue;
                if (payload == "[DONE]")
                    break;

                if (ParseChunk(payload) is not { } chunk)
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

    private sealed record ChatMessagePartDto(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text);

    private sealed record ChatMessageDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("parts")] IReadOnlyList<ChatMessagePartDto> Parts);

    private sealed record ChatRequestDto(
        [property: JsonPropertyName("transcriptionId")] string TranscriptionId,
        [property: JsonPropertyName("message")] ChatMessageDto Message);
}
