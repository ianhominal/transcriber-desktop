using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente de "Formatos" (instrucciones reutilizables de IA sobre una transcripción, ver
/// <c>src/lib/recipes/*</c> del backend web): lista los formatos guardados del usuario
/// (<c>GET /api/recipes</c>) y aplica uno a una transcripción puntual
/// (<c>POST /api/recipes/apply</c>), que responde en STREAMING de texto plano
/// (<c>result.toTextStreamResponse()</c> del AI SDK -- sin ningún envoltorio/protocolo: los bytes
/// que llegan SON el texto generado, a diferencia de <c>/api/chat</c>, que usa el protocolo
/// UIMessage/SSE del AI SDK -- ver <see cref="AiChatClient"/> para ese parseo). Mismo criterio de
/// autenticación que <see cref="AiSummaryClient"/>.
/// </summary>
public sealed class AiRecipesClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public AiRecipesClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Parsea <c>{ recipes: [{ id, name, instruction, isDefault, createdAt }] }</c>. Lógica pura.
    /// Nunca lanza: un JSON inválido o con forma inesperada devuelve una lista vacía (mismo
    /// criterio best-effort que <c>listRecipes</c> del lado del backend).
    /// </summary>
    public static IReadOnlyList<AiRecipeDto> ParseListResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recipes", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<AiRecipeDto>();

            var result = new List<AiRecipeDto>();
            foreach (var item in arr.EnumerateArray())
            {
                if (TryParseRecipeItem(item) is { } recipe)
                    result.Add(recipe);
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<AiRecipeDto>();
        }
    }

    /// <summary>
    /// Parsea la respuesta de un solo formato: <c>{ recipe: { id, name, instruction, isDefault } }</c>
    /// -- forma que devuelven <c>POST /api/recipes</c> (crear) y <c>PATCH /api/recipes/[id]</c>
    /// (editar/marcar default). Lanza <see cref="AiAssistException"/> si el JSON es inválido o no
    /// trae un "recipe" con forma mínima utilizable (a diferencia de <see cref="ParseListResponse"/>,
    /// acá SÍ importa fallar fuerte: es la confirmación de una escritura que el usuario espera ver
    /// reflejada, no un listado best-effort).
    /// </summary>
    public static AiRecipeDto ParseRecipeResponse(string json)
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
            if (!doc.RootElement.TryGetProperty("recipe", out var item) || TryParseRecipeItem(item) is not { } recipe)
                throw new AiAssistException("El servidor no devolvió el formato guardado.");
            return recipe;
        }
    }

    private static AiRecipeDto? TryParseRecipeItem(JsonElement item)
    {
        var id = GetString(item, "id");
        var name = GetString(item, "name");
        if (id is null || name is null)
            return null; // fila sin forma mínima utilizable: se ignora, no rompe el resto de la lista.

        var instruction = GetString(item, "instruction") ?? string.Empty;
        var isDefault = item.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True;
        return new AiRecipeDto(id, name, instruction, isDefault);
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>Arma el cuerpo JSON del POST de aplicar: <c>{ "transcriptionId", "recipeId" }</c>.</summary>
    public static string BuildApplyRequestBody(string transcriptionId, string recipeId) =>
        JsonSerializer.Serialize(new ApplyRecipeRequestDto(transcriptionId, recipeId));

    /// <summary>Arma el cuerpo JSON de crear/editar: <c>{ "name", "instruction" }</c> (mismo shape para <c>POST /api/recipes</c> y <c>PATCH /api/recipes/[id]</c>).</summary>
    public static string BuildSaveRequestBody(string name, string instruction) =>
        JsonSerializer.Serialize(new SaveRecipeRequestDto(name, instruction));

    /// <summary>Arma el cuerpo JSON de marcar default: <c>{ "isDefault": true }</c>.</summary>
    public static string BuildSetDefaultRequestBody() => JsonSerializer.Serialize(new SetDefaultRecipeRequestDto(true));

    /// <summary>Mismo criterio de mensajes amigables en español que <see cref="AiSummaryClient.BuildErrorMessage"/>.</summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.",
            HttpStatusCode.TooManyRequests => detail.Length > 0 ? detail : "Llegaste al límite de formatos aplicados por hoy. Probá de nuevo mañana.",
            HttpStatusCode.NotFound => detail.Length > 0 ? detail : "No se encontró el formato o la transcripción.",
            // 400 (validación: nombre/instrucción vacíos o de más, tope de 30 formatos) -- el backend
            // ya manda el mensaje en español listo para mostrar (ver sanitizeName/sanitizeInstruction/
            // canAddRecipe en src/lib/recipes/validate.ts), así que no hace falta envolverlo.
            HttpStatusCode.BadRequest => detail.Length > 0 ? detail : "El formato no es válido.",
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

    /// <summary>Lista los formatos guardados del usuario logueado.</summary>
    public async Task<IReadOnlyList<AiRecipeDto>> ListRecipesAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para listar formatos.", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/recipes");
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

    /// <summary>
    /// Aplica el formato <paramref name="recipeId"/> a <paramref name="transcriptionId"/>. El
    /// backend responde en streaming de texto plano (sin envoltorio JSON por chunk): se manda con
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> para no esperar el cuerpo completo, y
    /// cada porción leída se reporta ACUMULADA (el texto completo recibido hasta el momento, mismo
    /// criterio que ya usa <c>MainViewModel.TranscribeAsync</c> para pintar el streaming de
    /// segmentos de Whisper con un <c>StringBuilder</c>) vía <paramref name="onProgress"/>, para que
    /// el caller solo tenga que pintar el valor recibido sin mantener su propio acumulador.
    /// </summary>
    public async Task<string> ApplyRecipeAsync(
        string transcriptionId, string recipeId, string accessToken,
        IProgress<string>? onProgress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para aplicar un formato.", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/recipes/apply");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(BuildApplyRequestBody(transcriptionId, recipeId), Encoding.UTF8, "application/json");

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
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, ct)) > 0)
            {
                sb.Append(buffer, 0, read);
                onProgress?.Report(sb.ToString());
            }
            return sb.ToString();
        }
    }

    /// <summary>Crea un formato nuevo. Body <c>{ name, instruction }</c>, respuesta 201 <c>{ recipe }</c>.</summary>
    public async Task<AiRecipeDto> CreateRecipeAsync(string name, string instruction, string accessToken, CancellationToken ct) =>
        await SendSaveRequestAsync(HttpMethod.Post, $"{_baseUrl}/api/recipes", BuildSaveRequestBody(name, instruction), accessToken, ct);

    /// <summary>Edita nombre/instrucción de un formato existente. Body <c>{ name, instruction }</c>, respuesta <c>{ recipe }</c>.</summary>
    public async Task<AiRecipeDto> UpdateRecipeAsync(string recipeId, string name, string instruction, string accessToken, CancellationToken ct) =>
        await SendSaveRequestAsync(HttpMethod.Patch, $"{_baseUrl}/api/recipes/{recipeId}", BuildSaveRequestBody(name, instruction), accessToken, ct);

    /// <summary>Marca <paramref name="recipeId"/> como el formato default del usuario (desmarca cualquier otro, ver backend). Body <c>{ isDefault: true }</c>.</summary>
    public async Task<AiRecipeDto> SetDefaultRecipeAsync(string recipeId, string accessToken, CancellationToken ct) =>
        await SendSaveRequestAsync(HttpMethod.Patch, $"{_baseUrl}/api/recipes/{recipeId}", BuildSetDefaultRequestBody(), accessToken, ct);

    /// <summary>POST/PATCH compartidos por crear, editar y marcar-default: mismo shape de respuesta <c>{ recipe }</c> en los tres.</summary>
    private async Task<AiRecipeDto> SendSaveRequestAsync(HttpMethod method, string url, string requestBody, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para guardar el formato.", nameof(accessToken));

        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

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
            return ParseRecipeResponse(body);
        }
    }

    /// <summary>Borra un formato. Requiere sesión y ownership (scopeado a <c>userId</c> además de RLS, del lado del backend).</summary>
    public async Task DeleteRecipeAsync(string recipeId, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Falta la sesión para borrar el formato.", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/recipes/{recipeId}");
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
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new AiAssistException(BuildErrorMessage(response.StatusCode, body));
            }
        }
    }

    private sealed record ApplyRecipeRequestDto(
        [property: JsonPropertyName("transcriptionId")] string TranscriptionId,
        [property: JsonPropertyName("recipeId")] string RecipeId);

    private sealed record SaveRecipeRequestDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("instruction")] string Instruction);

    private sealed record SetDefaultRecipeRequestDto(
        [property: JsonPropertyName("isDefault")] bool IsDefault);
}

/// <summary>Un formato guardado por el usuario (ver <c>src/lib/recipes/types.ts</c> del backend web).</summary>
public sealed record AiRecipeDto(string Id, string Name, string Instruction, bool IsDefault);
