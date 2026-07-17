using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AudioTranscriber.Core.Sync;

/// <summary>Cliente del backend SaaS: sync pull/push con token Bearer.</summary>
public sealed class SyncApiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public SyncApiClient(HttpClient http, string backendBaseUrl)
    {
        _http = http;
        _baseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>Trae los cambios remotos desde `since` (null = pull completo).</summary>
    public async Task<PullResponse> PullAsync(string accessToken, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/sync/pull";
        if (since is not null)
            url += $"?since={Uri.EscapeDataString(since.Value.UtcDateTime.ToString("o"))}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException($"pull falló ({(int)resp.StatusCode}): {json}", (int)resp.StatusCode);

        return JsonSerializer.Deserialize<PullResponse>(json) ?? new PullResponse();
    }

    /// <summary>
    /// Envía cambios de metadata (crear/renombrar/borrar proyectos, editar/borrar transcripciones).
    /// El backend contesta 200 incluso cuando hubo errores por ítem (ver <see cref="PushResponse"/>),
    /// así que el body se parsea y se devuelve siempre -- <see cref="SyncApiException"/> queda
    /// reservado para fallos de transporte/auth (status no exitoso).
    /// </summary>
    public async Task<PushResponse> PushAsync(string accessToken, PushRequest payload, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/sync/push");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = JsonContent.Create(payload);

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncApiException($"push falló ({(int)resp.StatusCode}): {json}", (int)resp.StatusCode);

        return JsonSerializer.Deserialize<PushResponse>(json) ?? new PushResponse();
    }
}

public sealed class SyncApiException : Exception
{
    /// <summary>
    /// Código HTTP de la respuesta cuando la excepción vino de un pull/push/subida no exitosos
    /// (p.ej. 401/403 = access token inválido/vencido rechazado por el backend). Null para otros
    /// errores (red, timeout, deserialización). Permite a <see cref="SyncApiException"/>
    /// distinguir programáticamente una sesión inválida de un error genérico (500, rate limit,
    /// etc.), igual que ya hace <see cref="SyncAuthException.StatusCode"/> para el refresh.
    /// </summary>
    public int? StatusCode { get; }

    public SyncApiException(string message) : base(message) { }
    public SyncApiException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
}
