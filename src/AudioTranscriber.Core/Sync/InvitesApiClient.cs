using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Cliente de invitaciones (Team Sharing slice 1b, Phase 16, design ADR-13 "paridad de invitaciones
/// pendientes en los dos clientes"). Consume EXACTAMENTE el mismo contrato JSON que ya expone la
/// web (<c>web/src/app/api/invites/route.ts</c> Phase 9.5, <c>web/src/app/api/invites/[id]/route.ts</c>
/// Phase 9.3) -- el modelo GitHub de invitaciones (<c>pending → accepted | rejected</c>, membresía
/// creada recién al aceptar) vive del lado del servidor; este cliente no reimplementa nada de esa
/// lógica, solo la consume. Sigue el mismo patrón que <see cref="SyncApiClient"/>: <see cref="SyncApiException"/>
/// para fallos de transporte/auth, sin lógica de permisos acá (el desktop es "tonto": muestra lo que
/// el servidor manda y deja que el servidor rechace).
/// </summary>
public sealed class InvitesApiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public InvitesApiClient(HttpClient http, string backendBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Trae las invitaciones `pending` RECIBIDAS por el usuario actual -- <c>GET /api/invites</c>
    /// sin <c>?projectId=</c> (ver Phase 9.5: la otra vista del mismo endpoint, "enviadas", queda
    /// fuera de Phase 16 porque el desktop no tiene UI de compartir todavía, solo de resolver lo
    /// que le llega).
    /// </summary>
    public async Task<List<InviteDto>> GetPendingInvitesAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/invites");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException($"listar invitaciones falló ({(int)resp.StatusCode}): {json}", (int)resp.StatusCode);

        var parsed = JsonSerializer.Deserialize<ListInvitesResponse>(json);
        return parsed?.Invites ?? new List<InviteDto>();
    }

    /// <summary>
    /// Trae las invitaciones `pending` ENVIADAS para un proyecto -- <c>GET /api/invites?projectId=</c>
    /// (Phase 9.5: "la otra vista del mismo endpoint", fuera de alcance en Phase 16, ahora en
    /// alcance para <c>ShareWindow</c>, Phase 17). La RLS de <c>project_invites</c> (capability
    /// <c>share</c>) es la única que scopea esta vista; este cliente no repite ese chequeo.
    /// </summary>
    public async Task<List<InviteDto>> GetSentInvitesAsync(string accessToken, string projectId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/invites?projectId={Uri.EscapeDataString(projectId)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException($"listar invitaciones enviadas falló ({(int)resp.StatusCode}): {json}", (int)resp.StatusCode);

        var parsed = JsonSerializer.Deserialize<ListInvitesResponse>(json);
        return parsed?.Invites ?? new List<InviteDto>();
    }

    /// <summary>
    /// Cancela una invitación que vos mandaste -- <c>DELETE /api/invites/{id}</c>. El invitado no
    /// necesita hacer nada (spec "cancelar no requiere acción del invitado"); el servidor la borra
    /// directo (ver <c>web/src/lib/invites/store.ts cancelInvite</c>).
    /// </summary>
    public async Task CancelInviteAsync(string accessToken, string inviteId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/invites/{inviteId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException($"cancelar invitación falló ({(int)resp.StatusCode}): {json}", (int)resp.StatusCode);
    }

    /// <summary>
    /// Acepta o rechaza una invitación propia -- <c>POST /api/invites/{id}</c> body
    /// <c>{ action: "accept" | "reject" }</c> (Phase 9.3). La membresía se crea del lado del
    /// servidor recién al aceptar (RPC <c>accept_project_invite</c>, transaccional); este cliente
    /// no toca <c>project_members</c> ni decide nada, solo manda la acción y deja que el servidor
    /// resuelva.
    /// </summary>
    public async Task RespondInviteAsync(string accessToken, string inviteId, string action, CancellationToken ct = default)
    {
        if (action != "accept" && action != "reject")
            throw new ArgumentException("Acción inválida: usá 'accept' o 'reject'.", nameof(action));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/invites/{inviteId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(new RespondInviteRequestDto(action));

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException($"responder invitación falló ({(int)resp.StatusCode}): {json}", (int)resp.StatusCode);
    }

    private sealed record RespondInviteRequestDto([property: JsonPropertyName("action")] string Action);
}

/// <summary>
/// Fila de <c>project_invites</c> tal como la devuelve el servidor (mismo shape que
/// <c>web/src/lib/invites/store.ts</c> <c>ProjectInvite</c>). <c>Role</c> queda como <see cref="string"/>
/// crudo a propósito (no un enum): el desktop nunca decide capabilities a partir de él, solo lo
/// muestra -- cualquier validación de "qué puede hacer ese rol" es responsabilidad exclusiva del
/// servidor (regla dura del pedido: cero lógica de permisos en el cliente).
/// </summary>
public sealed class InviteDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";

    /// <summary>
    /// Nombre del proyecto (Team Sharing slice 1b, Phase 17): <c>GET /api/invites</c> ahora lo
    /// manda además del id crudo. Nullable a propósito -- si por algún motivo el servidor todavía
    /// no lo mandara, <c>InviteVm</c> (App) cae al id como antes en vez de mostrar un campo vacío.
    /// </summary>
    [JsonPropertyName("project_name")] public string? ProjectName { get; set; }

    [JsonPropertyName("invited_user_id")] public string InvitedUserId { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("invited_by")] public string InvitedBy { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("resolved_at")] public DateTimeOffset? ResolvedAt { get; set; }
}

internal sealed class ListInvitesResponse
{
    [JsonPropertyName("invites")] public List<InviteDto> Invites { get; set; } = new();
}
