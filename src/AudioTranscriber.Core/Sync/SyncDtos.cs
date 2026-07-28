using System.Text.Json.Serialization;

namespace AudioTranscriber.Core.Sync;

/// <summary>Sesión devuelta por Supabase Auth (login / refresh).</summary>
public sealed class AuthSession
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";

    /// <summary>
    /// Vencimiento absoluto (unix seconds, UTC) del access token. Supabase Auth NO siempre
    /// incluye este campo en la respuesta de /auth/v1/token (la doc pública del self-hosting
    /// solo documenta "expires_in" para password/refresh_token) — cuando falta, queda en 0 acá
    /// hasta que se llama a <see cref="EnsureExpiresAt"/>.
    /// </summary>
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }

    /// <summary>Vencimiento relativo en segundos (siempre presente en la respuesta real de Supabase).</summary>
    [JsonPropertyName("expires_in")] public long? ExpiresIn { get; set; }

    [JsonPropertyName("user")] public AuthUser? User { get; set; }

    /// <summary>
    /// Garantiza que <see cref="ExpiresAt"/> quede con un vencimiento absoluto utilizable.
    /// Si "expires_at" ya vino con un valor válido (&gt; 0) se respeta tal cual; si no, se
    /// calcula como <paramref name="now"/> + "expires_in". Sin esto, un login/refresh donde el
    /// backend solo manda "expires_in" deja <see cref="ExpiresAt"/> en 0, y
    /// <see cref="TokenExpiryPolicy.ShouldRefresh"/> interpreta 0 como "siempre vencido": el
    /// cliente terminaría refrescando (y rotando el refresh token) en cada ciclo de sync.
    /// Lógica pura (recibe <paramref name="now"/> en vez de leer el reloj) para poder testearla
    /// sin mocks, igual que <see cref="TokenExpiryPolicy"/>.
    /// </summary>
    public void EnsureExpiresAt(DateTimeOffset now)
    {
        if (ExpiresAt > 0)
            return;

        if (ExpiresIn is > 0)
            ExpiresAt = now.ToUnixTimeSeconds() + ExpiresIn.Value;
    }
}

public sealed class AuthUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";

    /// <summary>
    /// Metadata del proveedor (Google, password, etc.). Supabase la manda tal cual la recibió del
    /// proveedor OAuth, así que los nombres de campo varían según cómo se inició sesión.
    /// </summary>
    [JsonPropertyName("user_metadata")] public UserMetadata? UserMetadata { get; set; }

    /// <summary>
    /// Nombre para mostrar en la UI. Google manda "full_name" y a veces también "name" (mismo
    /// valor, según el flujo); se prioriza "name" y se cae a "full_name". Si el proveedor no
    /// mandó ninguno de los dos, se usa el email como último recurso para no dejar la UI vacía.
    /// Lógica pura (sin I/O) para poder testearla sin mockear la sesión completa.
    /// </summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(UserMetadata?.Name) ? UserMetadata!.Name!
        : !string.IsNullOrWhiteSpace(UserMetadata?.FullName) ? UserMetadata!.FullName!
        : Email;

    /// <summary>
    /// URL de la foto de perfil. Google manda "avatar_url"; algunos flujos (o proveedores) usan
    /// "picture" en su lugar con el mismo valor. "" si no vino ninguno (la UI cae a un placeholder
    /// con iniciales en vez de romper con una URL vacía).
    /// </summary>
    [JsonIgnore]
    public string AvatarUrl =>
        !string.IsNullOrWhiteSpace(UserMetadata?.AvatarUrl) ? UserMetadata!.AvatarUrl!
        : !string.IsNullOrWhiteSpace(UserMetadata?.Picture) ? UserMetadata!.Picture!
        : "";

    /// <summary>
    /// Iniciales para el placeholder del avatar cuando no hay <see cref="AvatarUrl"/> (o falla la
    /// carga de la imagen); ver <see cref="UserProfileFormatter.GetInitials"/>.
    /// </summary>
    [JsonIgnore]
    public string Initials => UserProfileFormatter.GetInitials(DisplayName);
}

/// <summary>
/// Metadata de usuario tal como la devuelve Supabase Auth en "user_metadata" (pasa el JSON del
/// proveedor OAuth casi sin tocar). Todos los campos son opcionales: el proveedor password/email
/// no manda ninguno; Google suele mandar "full_name"/"name" y "avatar_url"/"picture".
/// </summary>
public sealed class UserMetadata
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("picture")] public string? Picture { get; set; }
}

// ---- DTOs de sync (coinciden con /api/sync/pull y /api/sync/push) ----

public sealed class RemoteProject
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Id del proyecto padre (uuid) o null si es raíz. Campo nuevo del contrato de sync; un
    /// servidor viejo que todavía no lo manda deserializa esto como null sin romper (opcional,
    /// backward-compatible).
    /// </summary>
    [JsonPropertyName("parent_project_id")] public string? ParentProjectId { get; set; }

    /// <summary>
    /// Origen del proyecto ('local' | 'drive') tal como lo manda el servidor. Solo informativo
    /// por ahora: el cliente todavía no cambia comportamiento según este valor. Opcional,
    /// backward-compatible con un servidor viejo.
    /// </summary>
    [JsonPropertyName("sync_origin")] public string? SyncOrigin { get; set; }

    /// <summary>
    /// version monotónico (ADR-07): incrementado por el mismo trigger que updated_at en cada
    /// UPDATE. Único árbitro de conflictos -- el reloj del cliente nunca decide un ganador (I-5).
    /// </summary>
    [JsonPropertyName("version")] public int Version { get; set; }
}

public sealed class RemoteTranscription
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("audio_name")] public string AudioName { get; set; } = "";
    [JsonPropertyName("audio_url_signed")] public string? AudioUrlSigned { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("deleted_at")] public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Ver <see cref="RemoteProject.Version"/> -- mismo contrato, misma semántica (ADR-07).</summary>
    [JsonPropertyName("version")] public int Version { get; set; }

    /// <summary>
    /// Estado de audio explícito (Team Sharing slice 1b, Phase 15, ADR-13/14): 'available' |
    /// 'pending_upload' | 'unavailable', calculado server-side (ver
    /// <c>web/src/lib/sync/audioState.ts</c>, <c>computeAudioState</c> -- único lugar que decide la
    /// regla, ADR-13 "un solo contrato de servidor, dos vistas"). Opcional, backward-compatible con
    /// un servidor viejo que todavía no lo manda (mismo criterio que <see cref="RemoteProject.ParentProjectId"/>).
    /// El desktop hoy no muestra este estado en la UI (Phase 15 cubre solo el DTO + la descarga ya
    /// existente basada en <see cref="AudioUrlSigned"/>); queda disponible para consumo futuro sin
    /// romper nada mientras tanto.
    /// </summary>
    [JsonPropertyName("audio_state")] public string? AudioState { get; set; }
}

public sealed class PullResponse
{
    [JsonPropertyName("serverTime")] public DateTimeOffset ServerTime { get; set; }
    [JsonPropertyName("projects")] public List<RemoteProject> Projects { get; set; } = new();
    [JsonPropertyName("transcriptions")] public List<RemoteTranscription> Transcriptions { get; set; } = new();
}

public sealed class PushRequest
{
    [JsonPropertyName("projects")] public PushBucket<ProjectUpsert>? Projects { get; set; }
    [JsonPropertyName("transcriptions")] public PushBucket<TranscriptionUpsert>? Transcriptions { get; set; }
}

public sealed class PushBucket<T>
{
    [JsonPropertyName("upserts")] public List<T> Upserts { get; set; } = new();
    [JsonPropertyName("deletes")] public List<string> Deletes { get; set; } = new();
}

/// <summary>
/// Respuesta de POST /api/sync/push (ver <c>api/sync/push/route.ts</c>): el backend siempre
/// contesta 200 con este cuerpo, incluso cuando hubo errores por ítem -- "ok" es solo
/// <c>errors.length === 0</c>. No hay ningún código/tipo estructurado por error: <see cref="Errors"/>
/// son mensajes de texto plano (en español), y la única forma de reaccionar a uno en particular
/// (p.ej. el rechazo de un borrado en cascada, ver <see cref="PushErrorHandling"/>) es matchear su
/// forma exacta.
/// </summary>
public sealed class PushResponse
{
    [JsonPropertyName("serverTime")] public DateTimeOffset? ServerTime { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Resultado estructurado por ítem (ADR-07c, Task 5.4): reemplaza el matcheo por string de
    /// <see cref="Errors"/> como canal de decisión. <see cref="Errors"/> se mantiene solo por
    /// compatibilidad de mensajes de UI. Vacío en un servidor viejo que todavía no lo manda
    /// (backward-compatible, ver <see cref="PushResultItem"/>).
    /// </summary>
    [JsonPropertyName("results")] public List<PushResultItem> Results { get; set; } = new();
}

/// <summary>
/// Un ítem de <see cref="PushResponse.Results"/> (ADR-07c). Forma exacta que ya devuelve el
/// backend (<c>web/src/app/api/sync/push/route.ts</c>, tipo <c>PushResult</c>):
/// <c>{status:"ok", version}</c> / <c>{status:"conflict", version}</c> /
/// <c>{status:"error", code}</c>. <see cref="Version"/> y <see cref="Code"/> son mutuamente
/// exclusivos según <see cref="Status"/> -- por eso ambos son nullable en vez de modelar tres
/// clases separadas (el contrato JSON real es un solo shape con campos opcionales).
/// </summary>
public sealed class PushResultItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary><c>"project"</c> o <c>"transcription"</c>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary><c>"ok"</c>, <c>"conflict"</c> o <c>"error"</c>.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";

    /// <summary>version resultante ("ok") o la del servidor que ganó ("conflict"). Ausente en "error".</summary>
    [JsonPropertyName("version")] public int? Version { get; set; }

    /// <summary>Código de error (p.ej. "client_too_old"). Solo presente cuando Status == "error".</summary>
    [JsonPropertyName("code")] public string? Code { get; set; }
}

public sealed class ProjectUpsert
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>
    /// Icono/emoji del proyecto. El desktop no tiene UI para editarlo (<c>AudioProject</c> no
    /// tiene ese campo en el dominio), así que acá siempre queda null. Mismo criterio que
    /// <see cref="ParentProjectId"/>: sin <see cref="JsonIgnoreCondition.WhenWritingNull"/> se
    /// mandaba <c>"icon":null</c> en CADA push, pisando cualquier emoji que el usuario haya
    /// elegido del lado web (bug C2).
    /// </summary>
    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; set; }

    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>
    /// Id del proyecto padre a asignar. IMPORTANTE (contrato del servidor): "undefined" (campo
    /// ausente del JSON) no toca el padre actual; "null" explícito lo desengancha. Por eso lleva
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/>: mientras el desktop no gestione
    /// jerarquía (no hay UI todavía), esta propiedad queda en null y por lo tanto se OMITE del
    /// JSON en vez de mandar "null" — si mandara "null" en cada push, desengancharía cualquier
    /// jerarquía que el usuario haya armado del lado web.
    /// </summary>
    [JsonPropertyName("parent_project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentProjectId { get; set; }

    /// <summary>
    /// version que el cliente cree que tiene la fila (ADR-07c/g): "undefined" (campo ausente) es un
    /// ítem NUEVO sin base contra la cual comparar -- el servidor lo trata como alta. Un valor
    /// explícito le permite al servidor rechazar el upsert como "conflict" si la fila real avanzó
    /// desde entonces. Igual criterio que <see cref="ParentProjectId"/>/<see cref="Icon"/>: NUNCA
    /// se manda un 0 falso para un ítem nuevo.
    /// </summary>
    [JsonPropertyName("base_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BaseVersion { get; set; }
}

public sealed class TranscriptionUpsert
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }

    // NOT NULL en la tabla del server: sin esto el backend no puede CREAR la fila de una
    // transcripción 100% LOCAL (motor Local, que nunca pasó por /api/transcribe/Groq) -- su push
    // solo podía ACTUALIZAR una fila que no existía, y se perdía en silencio. Ver el endpoint
    // /api/sync/push (repo web): con audio_name presente hace un upsert real (crea-o-actualiza).
    [JsonPropertyName("audio_name")] public string? AudioName { get; set; }

    /// <summary>Ver <see cref="ProjectUpsert.BaseVersion"/> -- mismo contrato, misma semántica (ADR-07c/g).</summary>
    [JsonPropertyName("base_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BaseVersion { get; set; }
}
