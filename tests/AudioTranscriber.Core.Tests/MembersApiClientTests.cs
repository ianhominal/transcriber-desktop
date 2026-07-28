using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Team Sharing slice 1b, Phase 17 (UI de compartir proyectos): cliente Core que consume el
/// contrato de miembros del proyecto (<c>GET/POST /api/projects/{projectId}/members</c>,
/// <c>PATCH/DELETE /api/projects/{projectId}/members/{userId}</c>). Mismo criterio que
/// <see cref="InvitesApiClient"/>: <see cref="SyncApiException"/> para fallos de transporte/auth,
/// sin lógica de permisos acá (el desktop es "tonto": muestra lo que el servidor manda y deja que
/// el servidor rechace).
/// </summary>
public class MembersApiClientTests
{
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
    public async Task GetMembers_MandaBearer_YParseaLaLista()
    {
        var handler = new FakeHandler(_ =>
            Json("""
                {"members":[{"user_id":"u1","email":"dueño@x.com","role":"owner"},
                {"user_id":"u2","email":"editor@x.com","role":"editor"}]}
                """));
        var client = new MembersApiClient(new HttpClient(handler), "https://app.vercel.app");

        var members = await client.GetMembersAsync("AT", "p1");

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("/api/projects/p1/members", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(2, members.Count);
        Assert.Equal("u1", members[0].UserId);
        Assert.Equal("owner", members[0].Role);
    }

    [Fact]
    public async Task GetMembers_Error_LanzaSyncApiExceptionConStatusCode()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"No autorizado."}""", HttpStatusCode.Unauthorized));
        var client = new MembersApiClient(new HttpClient(handler), "https://app.vercel.app");

        var ex = await Assert.ThrowsAsync<SyncApiException>(() => client.GetMembersAsync("bad", "p1"));

        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task InviteMember_MandaEmailYRolEnElBody()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true}"""));
        var client = new MembersApiClient(new HttpClient(handler), "https://app.vercel.app");

        await client.InviteMemberAsync("AT", "p1", "nueva@x.com", "editor");

        Assert.Contains("/api/projects/p1/members", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Contains("\"email\":\"nueva@x.com\"", handler.LastBody);
        Assert.Contains("\"role\":\"editor\"", handler.LastBody);
    }

    [Fact]
    public async Task InviteMember_PersonaSinCuenta_LanzaSyncApiExceptionCon404()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"error":"Esa persona todavía no tiene una cuenta en el producto."}""", HttpStatusCode.NotFound));
        var client = new MembersApiClient(new HttpClient(handler), "https://app.vercel.app");

        var ex = await Assert.ThrowsAsync<SyncApiException>(() => client.InviteMemberAsync("AT", "p1", "nadie@x.com", "viewer"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("no tiene una cuenta", ex.Message);
    }

    [Fact]
    public async Task ChangeRole_MandaPatchConElNuevoRolEnElBody()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true}"""));
        var client = new MembersApiClient(new HttpClient(handler), "https://app.vercel.app");

        await client.ChangeRoleAsync("AT", "p1", "u2", "admin");

        Assert.Contains("/api/projects/p1/members/u2", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Patch, handler.LastRequest.Method);
        Assert.Contains("\"role\":\"admin\"", handler.LastBody);
    }

    [Fact]
    public async Task RemoveMember_MandaDelete()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true}"""));
        var client = new MembersApiClient(new HttpClient(handler), "https://app.vercel.app");

        await client.RemoveMemberAsync("AT", "p1", "u2");

        Assert.Contains("/api/projects/p1/members/u2", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }

    [Fact]
    public async Task RemoveMember_Error_LanzaSyncApiExceptionConCuerpo()
    {
        var handler = new FakeHandler(_ =>
            Json("""{"error":"No podés sacar al dueño del proyecto."}""", HttpStatusCode.Forbidden));
        var client = new MembersApiClient(new HttpClient(handler), "https://app.vercel.app");

        var ex = await Assert.ThrowsAsync<SyncApiException>(() => client.RemoveMemberAsync("AT", "p1", "u1"));

        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("dueño del proyecto", ex.Message);
    }
}
