using System.Net;
using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Team Sharing slice 1b, Phase 16.1 (design ADR-13 "paridad de invitaciones pendientes"): cliente
/// Core que consume el MISMO contrato JSON que ya expone la web (Phase 9.5 <c>GET /api/invites</c>,
/// Phase 9.3 <c>POST /api/invites/[id]</c> <c>{ action: "accept" | "reject" }</c>). Sin lógica de
/// permisos acá (regla dura del pedido): el desktop solo pide/muestra lo que el servidor le manda y
/// deja que el servidor rechace.
/// </summary>
public class InvitesApiClientTests
{
    // Mismo handler falso que SyncApiClientTests -- ver ese archivo para el criterio.
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
    public async Task GetPendingInvites_MandaBearer_YParseaLaLista()
    {
        // Mismo shape que web/src/lib/invites/store.ts ProjectInvite.
        var handler = new FakeHandler(_ =>
            Json("""
                {"invites":[{"id":"inv-1","project_id":"p1","invited_user_id":"u2","role":"viewer",
                "invited_by":"u1","status":"pending","created_at":"2026-07-28T00:00:00Z","resolved_at":null}]}
                """));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        var invites = await client.GetPendingInvitesAsync("AT");

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("AT", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Contains("/api/invites", handler.LastRequest.RequestUri!.ToString());
        Assert.Single(invites);
        Assert.Equal("inv-1", invites[0].Id);
        Assert.Equal("p1", invites[0].ProjectId);
        Assert.Equal("viewer", invites[0].Role);
        Assert.Equal("pending", invites[0].Status);
        Assert.Null(invites[0].ResolvedAt);
    }

    [Fact]
    public async Task GetPendingInvites_ParseaElNombreDelProyecto()
    {
        // Team Sharing slice 1b, Phase 17: GET /api/invites ahora manda project_name además del
        // project_id crudo -- InvitesWindow ya no debe mostrar el UUID (ver InviteVm.ProjectName).
        var handler = new FakeHandler(_ =>
            Json("""
                {"invites":[{"id":"inv-1","project_id":"p1","project_name":"Reuniones de equipo",
                "invited_user_id":"u2","role":"viewer","invited_by":"u1","status":"pending",
                "created_at":"2026-07-28T00:00:00Z","resolved_at":null}]}
                """));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        var invites = await client.GetPendingInvitesAsync("AT");

        Assert.Equal("Reuniones de equipo", invites[0].ProjectName);
    }

    [Fact]
    public async Task GetPendingInvites_SinInvitaciones_DevuelveListaVacia()
    {
        var handler = new FakeHandler(_ => Json("""{"invites":[]}"""));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        var invites = await client.GetPendingInvitesAsync("AT");

        Assert.Empty(invites);
    }

    [Fact]
    public async Task GetPendingInvites_Error_LanzaSyncApiExceptionConStatusCode()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"No autorizado."}""", HttpStatusCode.Unauthorized));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        var ex = await Assert.ThrowsAsync<SyncApiException>(() => client.GetPendingInvitesAsync("bad"));

        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task RespondInvite_Accept_MandaBearerYActionEnElBody()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true}"""));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        await client.RespondInviteAsync("AT", "inv-1", "accept");

        Assert.Equal("AT", handler.LastRequest!.Headers.Authorization!.Parameter);
        Assert.Contains("/api/invites/inv-1", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"action\":\"accept\"", handler.LastBody);
    }

    [Fact]
    public async Task RespondInvite_Reject_MandaActionEnElBody()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true}"""));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        await client.RespondInviteAsync("AT", "inv-1", "reject");

        Assert.Contains("\"action\":\"reject\"", handler.LastBody);
    }

    [Fact]
    public async Task RespondInvite_AccionInvalida_LanzaSinPegarleAlServidor()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true}"""));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        await Assert.ThrowsAsync<ArgumentException>(() => client.RespondInviteAsync("AT", "inv-1", "cancel"));

        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task GetSentInvites_MandaProjectIdComoQueryYParseaLaLista()
    {
        // Team Sharing slice 1b, Phase 17: GET /api/invites?projectId= -- "la otra vista del mismo
        // endpoint" (Phase 9.5), ahora sí en alcance para ShareWindow ("invitaciones enviadas").
        var handler = new FakeHandler(_ =>
            Json("""
                {"invites":[{"id":"inv-2","project_id":"p1","invited_user_id":"u3","role":"editor",
                "invited_by":"u1","status":"pending","created_at":"2026-07-28T00:00:00Z","resolved_at":null}]}
                """));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        var invites = await client.GetSentInvitesAsync("AT", "p1");

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Contains("/api/invites?projectId=p1", handler.LastRequest.RequestUri!.ToString());
        Assert.Single(invites);
        Assert.Equal("inv-2", invites[0].Id);
    }

    [Fact]
    public async Task CancelInvite_MandaDelete()
    {
        var handler = new FakeHandler(_ => Json("""{"ok":true}"""));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        await client.CancelInviteAsync("AT", "inv-2");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Contains("/api/invites/inv-2", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task CancelInvite_Error_LanzaSyncApiExceptionConStatusCode()
    {
        var handler = new FakeHandler(_ => Json("""{"error":"Invitación no encontrada o ya resuelta."}""", HttpStatusCode.NotFound));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        var ex = await Assert.ThrowsAsync<SyncApiException>(() => client.CancelInviteAsync("AT", "inv-2"));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task RespondInvite_Error_LanzaSyncApiExceptionConCuerpo()
    {
        // El servidor contesta 404/500 con { error } cuando la invitación no existe o ya se
        // resolvió (ver acceptInvite/rejectInvite en web/src/lib/invites/store.ts) -- el mensaje
        // real tiene que llegar al log/UI, mismo criterio que SyncApiClient.
        var handler = new FakeHandler(_ =>
            Json("""{"error":"Invitación no encontrada o ya resuelta."}""", HttpStatusCode.NotFound));
        var client = new InvitesApiClient(new HttpClient(handler), "https://app.vercel.app");

        var ex = await Assert.ThrowsAsync<SyncApiException>(() => client.RespondInviteAsync("AT", "inv-1", "accept"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("Invitación no encontrada o ya resuelta.", ex.Message);
    }
}
