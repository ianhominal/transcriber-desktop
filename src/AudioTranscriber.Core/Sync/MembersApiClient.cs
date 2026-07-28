using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente de miembros de proyecto (Team Sharing slice 1b, Phase 17 "UI de compartir proyectos").
/// Consume <c>GET/POST /api/projects/{projectId}/members</c> y
/// <c>PATCH/DELETE /api/projects/{projectId}/members/{userId}</c>. Mismo patrón que
/// <see cref="InvitesApiClient"/>: <see cref="SyncApiException"/> para fallos de transporte/auth,
/// cero lógica de permisos acá -- el desktop pide/muestra lo que el servidor manda y deja que el
/// servidor rechace (p.ej. degradar o sacar al dueño, o invitar a alguien sin cuenta).
/// </summary>
public sealed class MembersApiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public MembersApiClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>Lista los miembros actuales del proyecto -- <c>GET /api/projects/{projectId}/members</c>.</summary>
    public async Task<List<MemberDto>> GetMembersAsync(string accessToken, string projectId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/projects/{projectId}/members");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException(BuildErrorMessage("listar miembros", (int)resp.StatusCode, json), (int)resp.StatusCode);

        var parsed = JsonSerializer.Deserialize<ListMembersResponse>(json);
        return parsed?.Members ?? new List<MemberDto>();
    }

    /// <summary>
    /// Invita a alguien al proyecto por email -- <c>POST /api/projects/{projectId}/members</c>
    /// body <c>{ email, role }</c>. El servidor devuelve 404 si esa persona no tiene cuenta todavía
    /// (modelo GitHub, mismo criterio que <see cref="InvitesApiClient"/>): este cliente no
    /// reintenta ni adivina, solo propaga el mensaje del servidor.
    /// </summary>
    public async Task InviteMemberAsync(string accessToken, string projectId, string email, string role, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/projects/{projectId}/members");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(new InviteMemberRequestDto(email, role));

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException(BuildErrorMessage("invitar", (int)resp.StatusCode, json), (int)resp.StatusCode);
    }

    /// <summary>Cambia el rol de un miembro -- <c>PATCH /api/projects/{projectId}/members/{userId}</c> body <c>{ role }</c>.</summary>
    public async Task ChangeRoleAsync(string accessToken, string projectId, string userId, string role, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"{_baseUrl}/api/projects/{projectId}/members/{userId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(new ChangeRoleRequestDto(role));

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException(BuildErrorMessage("cambiar rol", (int)resp.StatusCode, json), (int)resp.StatusCode);
    }

    /// <summary>Quita a un miembro del proyecto -- <c>DELETE /api/projects/{projectId}/members/{userId}</c>.</summary>
    public async Task RemoveMemberAsync(string accessToken, string projectId, string userId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/projects/{projectId}/members/{userId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException(BuildErrorMessage("quitar miembro", (int)resp.StatusCode, json), (int)resp.StatusCode);
    }

    /// <summary>
    /// Arma el mensaje de <see cref="SyncApiException"/>: si el cuerpo es <c>{"error":"..."}</c>
    /// (el shape que usan todas las rutas de la web, ver <c>web/src/lib/invites/store.ts</c>), usa
    /// ESE texto solo -- ya es human-readable, pensado para mostrarse tal cual (regla dura del
    /// pedido: "nunca una ventana muda ni un stack trace"). Si no matchea ese shape, cae al cuerpo
    /// crudo para no perder información de diagnóstico.
    /// </summary>
    private static string BuildErrorMessage(string action, int statusCode, string json)
    {
        var detail = TryExtractServerError(json);
        return detail is not null
            ? $"{action} falló ({statusCode}): {detail}"
            : $"{action} falló ({statusCode}): {json}";
    }

    private static string? TryExtractServerError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.String)
                return errorProp.GetString();
        }
        catch (JsonException)
        {
            // Cuerpo no-JSON (p.ej. HTML de un 502) -- se usa el crudo, ver BuildErrorMessage.
        }
        return null;
    }

    private sealed record InviteMemberRequestDto(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("role")] string Role);

    private sealed record ChangeRoleRequestDto([property: JsonPropertyName("role")] string Role);
}

/// <summary>
/// Fila de <c>project_members</c> tal como la devuelve el servidor. <c>Role</c> queda como
/// <see cref="string"/> crudo a propósito (no un enum), mismo criterio que <see cref="InviteDto"/>:
/// el desktop nunca decide capabilities a partir de él, solo lo muestra.
/// </summary>
public sealed class MemberDto
{
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
}

internal sealed class ListMembersResponse
{
    [JsonPropertyName("members")] public List<MemberDto> Members { get; set; } = new();
}
