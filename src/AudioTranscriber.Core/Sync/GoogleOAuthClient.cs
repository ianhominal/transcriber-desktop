using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Login con Google via Supabase Auth, usando Authorization Code + PKCE con redirect loopback
/// (patrón estándar para apps nativas/desktop, ver RFC 8252 y la guía de PKCE de Supabase:
/// https://supabase.com/docs/guides/auth/sessions/pkce-flow).
///
/// Flujo:
/// 1. Genera code_verifier/code_challenge (<see cref="PkceHelper"/>).
/// 2. Levanta un HttpListener en 127.0.0.1 (con fallback de puertos) y abre el navegador
///    default del usuario contra {SUPABASE_URL}/auth/v1/authorize?provider=google&amp;...
/// 3. Supabase redirige a Google, el usuario inicia sesión, y Google vuelve a Supabase, que
///    finalmente redirige al navegador hacia nuestro loopback local con ?code=... en la query.
/// 4. Cambiamos ese code por una sesión en {SUPABASE_URL}/auth/v1/token?grant_type=pkce.
///
/// IMPORTANTE (paso manual, no automatizable desde acá): cada una de las URLs de
/// <see cref="LoopbackPorts"/> + "/callback" debe estar dada de alta en Supabase Dashboard ->
/// Authentication -> URL Configuration -> Redirect URLs. Por ejemplo, para el puerto primario:
/// http://127.0.0.1:53682/callback
/// </summary>
public sealed class GoogleOAuthClient
{
    // Puerto primario + fallbacks si ya está en uso. Todas estas URLs (host:puerto/callback)
    // deben estar dadas de alta en el allowlist de redirect URLs de Supabase.
    private static readonly int[] LoopbackPorts = { 53682, 53683, 53684, 53685 };

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public GoogleOAuthClient(HttpClient http, string supabaseUrl, string apiKey)
    {
        _http = http;
        _baseUrl = supabaseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    /// <summary>
    /// Ejecuta el flujo completo: abre el navegador, espera el redirect y devuelve la sesión ya
    /// canjeada. Lanza <see cref="SyncAuthException"/> ante cualquier error (navegador, timeout,
    /// error de Google/Supabase, o falla de red al canjear el code).
    /// </summary>
    public async Task<AuthSession> SignInAsync(CancellationToken ct = default)
    {
        var codeVerifier = PkceHelper.GenerateCodeVerifier();
        var codeChallenge = PkceHelper.CreateCodeChallenge(codeVerifier);

        using var listener = new HttpListener();
        var port = StartListener(listener);
        var redirectTo = $"http://127.0.0.1:{port}/callback";
        var authorizeUrl = BuildAuthorizeUrl(redirectTo, codeChallenge);

        try
        {
            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            listener.Stop();
            throw new SyncAuthException($"No se pudo abrir el navegador para iniciar sesión con Google: {ex.Message}", ex);
        }

        using var timeoutCts = new CancellationTokenSource(SignInTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        string code;
        try
        {
            code = await WaitForRedirectAsync(listener, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new SyncAuthException("Se agotó el tiempo de espera para iniciar sesión con Google.");
        }
        finally
        {
            listener.Stop();
        }

        return await ExchangeCodeAsync(code, codeVerifier, ct);
    }

    private string BuildAuthorizeUrl(string redirectTo, string codeChallenge) =>
        $"{_baseUrl}/auth/v1/authorize" +
        "?provider=google" +
        $"&redirect_to={Uri.EscapeDataString(redirectTo)}" +
        $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
        "&code_challenge_method=s256";

    private static int StartListener(HttpListener listener)
    {
        Exception? lastError = null;
        foreach (var port in LoopbackPorts)
        {
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return port;
            }
            catch (HttpListenerException ex)
            {
                lastError = ex; // puerto ocupado (u otro problema del SO): probamos el siguiente
            }
        }

        throw new SyncAuthException(
            $"No se pudo abrir un puerto local para el login con Google (probados: {string.Join(", ", LoopbackPorts)}).",
            lastError);
    }

    private static async Task<string> WaitForRedirectAsync(HttpListener listener, CancellationToken ct)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            var request = context.Request;
            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";

            if (path != "/callback")
            {
                await RespondAsync(context, HttpStatusCode.NotFound, NotFoundHtml);
                continue;
            }

            var query = ParseQuery(request.Url!.Query);
            query.TryGetValue("error", out var error);
            query.TryGetValue("error_description", out var errorDescription);
            query.TryGetValue("code", out var code);

            if (!string.IsNullOrEmpty(error))
            {
                await RespondAsync(context, HttpStatusCode.OK, ErrorHtml);
                throw new SyncAuthException($"Google rechazó el login: {errorDescription ?? error}");
            }

            if (string.IsNullOrEmpty(code))
            {
                await RespondAsync(context, HttpStatusCode.BadRequest, ErrorHtml);
                throw new SyncAuthException("La respuesta de Google no incluyó un código de autorización.");
            }

            await RespondAsync(context, HttpStatusCode.OK, SuccessHtml);
            return code;
        }
    }

    private async Task<AuthSession> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/auth/v1/token?grant_type=pkce");
        req.Headers.Add("apikey", _apiKey);
        req.Content = JsonContent.Create(new { auth_code = code, code_verifier = codeVerifier });

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new SyncAuthException($"Autenticación con Google falló ({(int)resp.StatusCode}).");

        var session = JsonSerializer.Deserialize<AuthSession>(json)
               ?? throw new SyncAuthException("Respuesta de autenticación vacía.");
        // Mismo fallback que SupabaseAuthClient.PostTokenAsync: Supabase no siempre manda
        // "expires_at" (ver AuthSession.EnsureExpiresAt), y este login también persiste
        // session.ExpiresAt vía SecureStore (LoginWindow.OnGoogleSignIn).
        session.EnsureExpiresAt(DateTimeOffset.UtcNow);
        return session;
    }

    private static async Task RespondAsync(HttpListenerContext context, HttpStatusCode status, string html)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.OutputStream.Close();
        }
        catch
        {
            // El navegador puede haber cerrado la conexión antes de tiempo; no es fatal.
        }
    }

    /// <summary>Parser mínimo de query string (evita depender de System.Web.HttpUtility).</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
            return result;

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx >= 0 ? pair[..idx] : pair;
            var value = idx >= 0 ? pair[(idx + 1)..] : "";
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return result;
    }

    private const string SuccessHtml = """
        <!doctype html>
        <html lang="es"><head><meta charset="utf-8"><title>AudioTranscriber</title></head>
        <body style="font-family:Segoe UI,sans-serif;text-align:center;margin-top:80px;">
        <h2>Sesión iniciada</h2>
        <p>Ya podés cerrar esta pestaña y volver a AudioTranscriber.</p>
        </body></html>
        """;

    private const string ErrorHtml = """
        <!doctype html>
        <html lang="es"><head><meta charset="utf-8"><title>AudioTranscriber</title></head>
        <body style="font-family:Segoe UI,sans-serif;text-align:center;margin-top:80px;">
        <h2>No se pudo iniciar sesión</h2>
        <p>Cerrá esta pestaña y volvé a intentarlo desde AudioTranscriber.</p>
        </body></html>
        """;

    private const string NotFoundHtml = """
        <!doctype html>
        <html lang="es"><head><meta charset="utf-8"><title>AudioTranscriber</title></head>
        <body></body></html>
        """;
}
