using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncApiClientTests
{
    // Handler falso que captura la request y devuelve una respuesta canned.
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null) LastBody = await request.Content.ReadAsStringAsync(ct);
            return _responder(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SignIn_ParseaTokensYUsuario()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"access_token":"AT","refresh_token":"RT","user":{"id":"u1","email":"a@b.com"}}"""));
        var auth = new SupabaseAuthClient(new HttpClient(handler), "https://x.supabase.co", "key");

        var session = await auth.SignInAsync("a@b.com", "pw");

        Assert.Equal("AT", session.AccessToken);
        Assert.Equal("RT", session.RefreshToken);
        Assert.Equal("u1", session.User!.Id);
    }

    [Fact]
    public async Task SignIn_CredencialesMalas_Lanza()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"invalid"}""", HttpStatusCode.BadRequest));
        var auth = new SupabaseAuthClient(new HttpClient(handler), "https://x.supabase.co", "key");

        await Assert.ThrowsAsync<SyncAuthException>(() => auth.SignInAsync("a@b.com", "bad"));
    }

    [Fact]
    public async Task SignIn_Error_IncluyeStatusCodeYCuerpoDeLaRespuesta()
    {
        // Antes el mensaje era genérico ("Autenticación falló (400).") y no traía el motivo real
        // que manda Supabase (p.ej. "Invalid Refresh Token"), así que sync.log no servía para
        // diagnosticar. Mismo criterio que SyncApiClient.PullAsync (ver test análogo abajo).
        var handler = new FakeHandler(_ =>
            Json("""{"error":"invalid_grant","error_description":"Invalid login credentials"}""", HttpStatusCode.BadRequest));
        var auth = new SupabaseAuthClient(new HttpClient(handler), "https://x.supabase.co", "key");

        var ex = await Assert.ThrowsAsync<SyncAuthException>(() => auth.SignInAsync("a@b.com", "bad"));

        Assert.Contains("400", ex.Message);
        Assert.Contains("Invalid login credentials", ex.Message);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Refresh_MandaGrantTypeYRefreshTokenCorrectos()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"access_token":"AT2","refresh_token":"RT2","expires_in":3600,"user":{"id":"u1","email":"a@b.com"}}"""));
        var auth = new SupabaseAuthClient(new HttpClient(handler), "https://x.supabase.co", "key");

        var session = await auth.RefreshAsync("old-refresh-token");

        Assert.Contains("grant_type=refresh_token", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("old-refresh-token", handler.LastBody);
        Assert.Equal("AT2", session.AccessToken);
        Assert.Equal("RT2", session.RefreshToken);
    }

    [Fact]
    public async Task Refresh_SoloExpiresIn_PersisteExpiresAtAbsolutoNoCero()
    {
        // Regresión del bug raíz: Supabase Auth no siempre manda "expires_at" en la respuesta de
        // /auth/v1/token (solo "expires_in" está documentado para password/refresh_token). Si el
        // cliente solo mirara "expires_at", ExpiresAt quedaría en 0, TokenExpiryPolicy.ShouldRefresh
        // lo interpretaría como "siempre vencido", y el cliente refrescaría (rotando el refresh
        // token) en cada ciclo de sync hasta que Supabase lo rechazara con 400.
        var handler = new FakeHandler(_ =>
            Json("""{"access_token":"AT2","refresh_token":"RT2","expires_in":3600,"user":{"id":"u1","email":"a@b.com"}}"""));
        var auth = new SupabaseAuthClient(new HttpClient(handler), "https://x.supabase.co", "key");

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var session = await auth.RefreshAsync("old-refresh-token");
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.True(session.ExpiresAt >= before + 3600);
        Assert.True(session.ExpiresAt <= after + 3600);
    }

    [Fact]
    public async Task Refresh_Error_IncluyeStatusCodeYCuerpoDeLaRespuesta()
    {
        // Caso real del bug: Supabase rechaza un refresh token ya invalidado/rotado con 400. El
        // mensaje de la excepción debe traer el cuerpo real (motivo) para que sync.log sirva.
        var handler = new FakeHandler(_ =>
            Json("""{"error":"invalid_grant","error_description":"Invalid Refresh Token: Already Used"}""", HttpStatusCode.BadRequest));
        var auth = new SupabaseAuthClient(new HttpClient(handler), "https://x.supabase.co", "key");

        var ex = await Assert.ThrowsAsync<SyncAuthException>(() => auth.RefreshAsync("bad-refresh-token"));

        Assert.Contains("400", ex.Message);
        Assert.Contains("Invalid Refresh Token: Already Used", ex.Message);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task Pull_MandaBearer_YParseaProyectos()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"serverTime":"2026-07-06T00:00:00Z","projects":[{"id":"p1","name":"Trabajo","updated_at":"2026-07-06T00:00:00Z"}],"transcriptions":[]}"""));
        var client = new SyncApiClient(new HttpClient(handler), "https://app.vercel.app");

        var res = await client.PullAsync("AT");

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Single(res.Projects);
        Assert.Equal("Trabajo", res.Projects[0].Name);
    }

    [Fact]
    public async Task Pull_ConSince_AgregaQueryParam()
    {
        var handler = new FakeHandler(_ => Json("""{"serverTime":"2026-07-06T00:00:00Z","projects":[],"transcriptions":[]}"""));
        var client = new SyncApiClient(new HttpClient(handler), "https://app.vercel.app");

        await client.PullAsync("AT", DateTimeOffset.FromUnixTimeSeconds(1000));

        Assert.Contains("since=", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Push_EnviaBearer_YSerializaBody()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true,"errors":[]}"""));
        var client = new SyncApiClient(new HttpClient(handler), "https://app.vercel.app");
        var payload = new PushRequest
        {
            Projects = new PushBucket<ProjectUpsert>
            {
                Upserts = { new ProjectUpsert { Id = "p1", Name = "Trabajo" } },
            },
        };

        await client.PushAsync("AT", payload);

        Assert.Equal("AT", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Contains("Trabajo", handler.LastBody);
        Assert.Contains("p1", handler.LastBody);
    }

    [Fact]
    public async Task Pull_Error_LanzaSyncApiException()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"No autorizado."}""", HttpStatusCode.Unauthorized));
        var client = new SyncApiClient(new HttpClient(handler), "https://app.vercel.app");

        await Assert.ThrowsAsync<SyncApiException>(() => client.PullAsync("bad"));
    }

    [Fact]
    public async Task Pull_Error_IncluyeStatusCodeYCuerpoDeLaRespuesta()
    {
        // Regresión: el mensaje de SyncApiException debe traer el código HTTP Y el cuerpo real
        // de la respuesta del backend (motivo de auth, rate limit, error de Groq, etc.), no un
        // mensaje genérico — si no, el log/UI de detalle de error (SyncCoordinator.LastError)
        // no tiene nada útil que mostrar.
        var handler = new FakeHandler(_ => Json("""{"error":"Token expirado."}""", HttpStatusCode.Unauthorized));
        var client = new SyncApiClient(new HttpClient(handler), "https://app.vercel.app");

        var ex = await Assert.ThrowsAsync<SyncApiException>(() => client.PullAsync("bad"));

        Assert.Contains("401", ex.Message);
        Assert.Contains("Token expirado.", ex.Message);
    }

    [Fact]
    public async Task Pull_Error_ExponeStatusCode()
    {
        // Regresión: SyncApiException.StatusCode (nuevo) es lo que usa SyncCoordinator para
        // distinguir un 401/403 de sesión inválida (problema 4: debe llevar a "Iniciá sesión",
        // no al panel de error genérico) de cualquier otro fallo del backend.
        var handler = new FakeHandler(_ => Json("""{"error":"Token expirado."}""", HttpStatusCode.Unauthorized));
        var client = new SyncApiClient(new HttpClient(handler), "https://app.vercel.app");

        var ex = await Assert.ThrowsAsync<SyncApiException>(() => client.PullAsync("bad"));

        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task Push_ParseaOkYErrorsDeLaRespuesta()
    {
        // Bug C1: antes PushAsync devolvía Task (void) y descartaba el body entero -- el backend
        // siempre contesta 200 con { ok, errors[] } (ver api/sync/push/route.ts), así que CUALQUIER
        // error reportado ahí (incluido el rechazo de un borrado en cascada) se perdía en silencio.
        var handler = new FakeHandler(_ =>
            Json("""{"serverTime":"2026-07-07T00:00:00Z","ok":false,"errors":["Proyecto inválido: (sin id)"]}"""));
        var client = new SyncApiClient(new HttpClient(handler), "https://app.vercel.app");

        var response = await client.PushAsync("AT", new PushRequest());

        Assert.False(response.Ok);
        Assert.Single(response.Errors);
        Assert.Equal("Proyecto inválido: (sin id)", response.Errors[0]);
    }

    [Fact]
    public async Task Push_Error_ExponeStatusCodeYCuerpo()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Forbidden"}""", HttpStatusCode.Forbidden));
        var client = new SyncApiClient(new HttpClient(handler), "https://app.vercel.app");
        var payload = new PushRequest();

        var ex = await Assert.ThrowsAsync<SyncApiException>(() => client.PushAsync("bad", payload));

        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("Forbidden", ex.Message);
    }
}
