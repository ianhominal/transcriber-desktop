using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Construye el cuerpo JSON del POST a <c>/api/desktop-session</c> (backend web), que intercambia
/// un access/refresh token de Supabase por cookies de sesión reales vía <c>Set-Cookie</c> --
/// procesadas por el cookie jar NATIVO de WebView2 (nunca a mano acá, ver comentario en
/// <c>AiDetailWindow.xaml.cs</c>). Lógica pura separada del code-behind de la ventana para poder
/// testearla sin un WebView2 real.
/// </summary>
public static class DesktopSessionRequestBuilder
{
    /// <summary>Serializa <paramref name="accessToken"/>/<paramref name="refreshToken"/> con los
    /// nombres de campo exactos que espera el contrato del backend (snake_case).</summary>
    public static string BuildJsonBody(string accessToken, string refreshToken) =>
        JsonSerializer.Serialize(new DesktopSessionRequest(accessToken, refreshToken));

    private sealed record DesktopSessionRequest(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken);
}
