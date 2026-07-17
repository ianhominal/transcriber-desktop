using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente de "Unir notas" (combina varias transcripciones en un solo documento generado por IA,
/// ver <c>src/app/api/notes/merge/route.ts</c> del backend web, brief "Híbrido nativo" 2026-07-14).
/// <c>POST {backendBaseUrl}/api/notes/merge</c>, body <c>{ transcriptionIds: string[], instruction?
/// }</c>, respuesta en STREAMING DE TEXTO PLANO (<c>result.toTextStreamResponse()</c> -- sin
/// envoltorio/protocolo, mismo wire format que <see cref="AiRecipesClient.ApplyRecipeAsync"/>, a
/// diferencia del protocolo UIMessage/SSE de <see cref="AiChatClient"/>/<see cref="AiBrainClient"/>).
/// Dos headers propios en la respuesta (<c>X-Merge-Truncated</c>, <c>X-Merge-Included-Count</c>) que
/// el cliente lee ANTES de consumir el body streaming (mismo criterio que <c>merge-view.tsx</c> del
/// backend web).
/// </summary>
public sealed class AiMergeClient
{
    /// <summary>Mínimo y máximo de notas elegibles por unión (<c>canMergeNoteCount</c> del backend, <c>src/lib/merge/validate.ts</c>).</summary>
    public const int MinNoteCount = 2;
    public const int MaxNoteCount = 20;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AiMergeClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>True si la cantidad de notas elegidas es válida para unir (ver <see cref="MinNoteCount"/>/<see cref="MaxNoteCount"/>). Lógica pura.</summary>
    public static bool CanMergeNoteCount(int count) => count is >= MinNoteCount and <= MaxNoteCount;

    /// <summary>
    /// Arma el cuerpo JSON del POST: <c>{ "transcriptionIds": [...], "instruction": "..." }</c>.
    /// <paramref name="instruction"/> null/en blanco se manda como string vacío (el backend la
    /// sanitiza igual, ver <c>sanitizeMergeInstruction</c>). Lógica pura, sin red.
    /// </summary>
    public static string BuildRequestBody(IReadOnlyList<string> transcriptionIds, string? instruction) =>
        JsonSerializer.Serialize(new MergeRequestDto(transcriptionIds, instruction ?? string.Empty));

    /// <summary>Mismo criterio de mensajes amigables en español que <see cref="AiRecipesClient.BuildErrorMessage"/>.</summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.",
            HttpStatusCode.TooManyRequests => detail.Length > 0 ? detail : "Llegaste al límite diario de uniones. Probá de nuevo mañana.",
            HttpStatusCode.NotFound => detail.Length > 0 ? detail : "No pudimos encontrar alguna de las notas elegidas.",
            HttpStatusCode.BadRequest => detail.Length > 0 ? detail : "Elegí entre 2 y 20 notas para unir.",
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
    /// Parsea el header <c>X-Merge-Truncated</c> ("true"/"false"). Lógica pura -- nunca lanza:
    /// cualquier valor que no sea "true" (case-insensitive) se toma como false, mismo criterio
    /// best-effort del resto de los parsers de este cliente.
    /// </summary>
    public static bool ParseTruncatedHeader(string? value) =>
        bool.TryParse(value, out var truncated) && truncated;

    /// <summary>Parsea el header <c>X-Merge-Included-Count</c>. Lógica pura -- 0 si falta o no es un entero.</summary>
    public static int ParseIncludedCountHeader(string? value) =>
        int.TryParse(value, out var count) ? count : 0;

    /// <summary>
    /// Une <paramref name="transcriptionIds"/> en un solo documento generado por IA, con
    /// <paramref name="instruction"/> opcional (ej. "armá una minuta con decisiones"). Reporta el
    /// texto ACUMULADO por <paramref name="onProgress"/> a medida que llega el streaming de texto
    /// plano (mismo criterio que <see cref="AiRecipesClient.ApplyRecipeAsync"/>) y devuelve el
    /// resultado completo junto con los metadatos de truncamiento (headers, leídos ANTES de
    /// consumir el body -- <see cref="HttpCompletionOption.ResponseHeadersRead"/> ya deja los
    /// headers disponibles apenas llegan, sin esperar el body entero).
    /// </summary>
    public async Task<AiMergeResult> MergeNotesAsync(
        IReadOnlyList<string> transcriptionIds, string? instruction, string accessToken,
        IProgress<string>? onProgress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para unir notas.", nameof(accessToken));
        if (!CanMergeNoteCount(transcriptionIds.Count))
            throw new ArgumentException("Elegí entre 2 y 20 notas para unir.", nameof(transcriptionIds));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/notes/merge");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(BuildRequestBody(transcriptionIds, instruction), Encoding.UTF8, "application/json");

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

            var truncated = ParseTruncatedHeader(GetHeaderValue(response, "X-Merge-Truncated"));
            var includedCount = ParseIncludedCountHeader(GetHeaderValue(response, "X-Merge-Included-Count"));

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sb = new StringBuilder();
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, ct)) > 0)
            {
                sb.Append(buffer, 0, read);
                onProgress?.Report(sb.ToString());
            }

            return new AiMergeResult(sb.ToString(), truncated, includedCount);
        }
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private sealed record MergeRequestDto(
        [property: JsonPropertyName("transcriptionIds")] IReadOnlyList<string> TranscriptionIds,
        [property: JsonPropertyName("instruction")] string Instruction);
}

/// <summary>Resultado de <c>POST /api/notes/merge</c>: el documento unido, si se truncó contenido
/// (notas muy largas, ver <c>combineNoteTexts</c> del backend) y cuántas notas se incluyeron
/// efectivamente en el prompt final.</summary>
public sealed record AiMergeResult(string Text, bool Truncated, int IncludedCount);
