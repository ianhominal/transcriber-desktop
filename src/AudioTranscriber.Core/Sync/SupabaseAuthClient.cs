using System.Net.Http.Json;
using System.Text.Json;

namespace AudioTranscriber.Core.Sync;

/// <summary>Login/refresh contra Supabase Auth (REST). Guardá el token con DPAPI (SecureStore).</summary>
public sealed class SupabaseAuthClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public SupabaseAuthClient(HttpClient http, string supabaseUrl, string apiKey)
    {
        _http = http;
        _baseUrl = supabaseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public Task<AuthSession> SignInAsync(string email, string password, CancellationToken ct = default) =>
        PostTokenAsync("password", new { email, password }, ct);

    public Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        PostTokenAsync("refresh_token", new { refresh_token = refreshToken }, ct);

    private async Task<AuthSession> PostTokenAsync(string grantType, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/auth/v1/token?grant_type={grantType}");
        req.Headers.Add("apikey", _apiKey);
        req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncAuthException($"Autenticación falló ({(int)resp.StatusCode}): {json}", (int)resp.StatusCode);

        var session = JsonSerializer.Deserialize<AuthSession>(json)
               ?? throw new SyncAuthException("Respuesta de autenticación vacía.");
        session.EnsureExpiresAt(DateTimeOffset.UtcNow);
        return session;
    }
}

public sealed class SyncAuthException : Exception
{
    /// <summary>
    /// Código HTTP de la respuesta de Supabase Auth cuando la excepción vino de una respuesta
    /// no exitosa (p.ej. 400 = refresh token inválido/rotado). Null para otros errores (red,
    /// timeout, respuesta vacía, cancelación del login con Google, etc.).
    /// </summary>
    public int? StatusCode { get; }

    public SyncAuthException(string message) : base(message) { }
    public SyncAuthException(string message, Exception? inner) : base(message, inner) { }
    public SyncAuthException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
}
