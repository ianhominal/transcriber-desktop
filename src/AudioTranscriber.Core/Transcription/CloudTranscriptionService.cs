using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AudioTranscriber.Core.Transcription;

/// <summary>
/// Transcribe subiendo el audio al BACKEND propio (<c>POST {backendBaseUrl}/api/transcribe</c>),
/// que a su vez llama a Groq server-side con SU PROPIA API key. Reemplaza al viejo flujo del modo
/// "Groq (nube)" interactivo, que pegaba directo a api.groq.com con una key guardada en la PC del
/// usuario vía la clase <c>GroqTranscriptionService</c> (eliminada por este mismo cambio: ya no
/// tiene sentido que exista código cliente capaz de hablarle a Groq directo).
/// <para/>
/// Mismo endpoint y mismo criterio de autenticación (Bearer = access token de la sesión de
/// Supabase) que ya usa <c>SyncEngine.UploadAudioAsync</c> para las subidas automáticas del sync
/// -- acá se agrega el parseo de la respuesta <c>{ text, id }</c> porque el modo interactivo
/// necesita el texto YA, a diferencia del sync (que lo recibe después, en el próximo pull).
/// </summary>
public sealed class CloudTranscriptionService
{
    /// <summary>Modelos que el backend acepta (allowlist, mismo contrato que <c>/api/transcribe</c>).</summary>
    public static readonly IReadOnlySet<string> AllowedModels =
        new HashSet<string>(StringComparer.Ordinal) { "whisper-large-v3", "whisper-large-v3-turbo" };

    /// <summary>Default cuando no se pide modelo o se pide uno fuera de la allowlist.</summary>
    public const string DefaultModel = "whisper-large-v3";

    private readonly HttpClient _http;
    private readonly string _backendBaseUrl;

    public CloudTranscriptionService(HttpClient httpClient, string backendBaseUrl)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
            throw new ArgumentException("Falta la URL del backend.", nameof(backendBaseUrl));
        _backendBaseUrl = backendBaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Resuelve el modelo a mandar: si <paramref name="requested"/> está en la allowlist se usa
    /// tal cual; si no (null, vacío, o cualquier valor no reconocido) cae a
    /// <see cref="DefaultModel"/>. Lógica pura -- misma allowlist/default que valida el backend.
    /// </summary>
    public static string ResolveModel(string? requested) =>
        requested is not null && AllowedModels.Contains(requested) ? requested : DefaultModel;

    /// <summary>
    /// Sube <paramref name="audioPath"/> al backend y devuelve el texto transcripto.
    /// <paramref name="accessToken"/> es el access token de la sesión de Supabase (el mismo que usa
    /// el sync, NO una API key de Groq). <paramref name="onLog"/> reporta el avance.
    /// <para/>
    /// <paramref name="translate"/>/<paramref name="targetLanguage"/> (aditivo, 2026-07-14): cuando
    /// <paramref name="translate"/> es true, se agregan los campos <c>mode=translate</c> +
    /// <c>targetLanguage</c> al form -- MISMO contrato que <c>POST /api/transcribe</c>
    /// (<c>resolveTranscribeMode</c>/<c>resolveTranslationLanguage</c> en
    /// <c>src/lib/translate/languages.ts</c>, ver <see cref="TranslationOptions"/>). Cuando es
    /// false (default), el form queda IDÉNTICO al de antes -- el backend por su cuenta ya default-ea
    /// a <c>mode=transcribe</c> cuando el campo no viene, así que no hace falta mandarlo siempre.
    /// El modo Local (Whisper en la PC) NUNCA llega a este método -- solo lo usa el motor nube.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioPath,
        string accessToken,
        string? model,
        IProgress<string>? onLog,
        CancellationToken ct,
        bool translate = false,
        string? targetLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException(
                "Falta la sesión para transcribir en la nube.", nameof(accessToken));
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("No se encontró el audio a transcribir.", audioPath);

        var resolvedModel = ResolveModel(model);
        var sizeKb = new FileInfo(audioPath).Length / 1024.0;

        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(audioPath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(audioPath));
        form.Add(new StringContent(resolvedModel), "model");
        if (translate)
        {
            form.Add(new StringContent(TranslationOptions.ModeTranslate), "mode");
            form.Add(new StringContent(TranslationOptions.ResolveLanguage(targetLanguage)), "targetLanguage");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_backendBaseUrl}/api/transcribe")
        {
            Content = form,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        onLog?.Report($"Subiendo al servidor ({sizeKb:0} KB, modelo {resolvedModel})…");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudTranscriptionException(
                "No hay conexión con el servidor. Revisá tu internet e intentá de nuevo.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Bugfix 2026-07-10 (LOW): el HttpClient compartido (ver SyncCoordinator.Http) tiene un
            // timeout de 100s (default de HttpClient, nunca se lo overridea acá). Cuando ese timeout
            // se cumple -- audio grande, servidor lento -- .NET cancela el request internamente y
            // tira OperationCanceledException/TaskCanceledException, EXACTAMENTE el mismo tipo que
            // se tira cuando el USUARIO cancela desde el botón "Cancelar" (MainViewModel pasa su
            // propio CancellationToken como `ct`). Antes, MainViewModel atrapaba cualquier
            // OperationCanceledException con "Transcripción cancelada." -- un timeout real se
            // mostraba como si el usuario hubiera cancelado, cuando no lo hizo.
            // Distinción robusta (recomendada por la documentación de HttpClient): si `ct` -- el
            // token que WE pasamos, controlado por el usuario -- NO fue el que pidió la
            // cancelación, la cancelación vino de adentro de HttpClient (el timeout). Convertirlo acá
            // en una CloudTranscriptionException (no deriva de OperationCanceledException) para que
            // el catch de MainViewModel la trate como cualquier otro error, con un mensaje que dice
            // la verdad, en vez de caer en el catch de cancelación.
            throw new CloudTranscriptionException(
                "El servidor tardó demasiado en responder. Probá de nuevo en unos minutos.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new CloudTranscriptionException(BuildErrorMessage(response.StatusCode, body));

            var result = ParseResponse(body);
            onLog?.Report("Transcripción recibida del servidor.");
            // translationWarning (2026-07-14): el backend traduce best-effort -- si la traducción
            // falla, IGUAL guarda y devuelve la transcripción original (nunca se pierde el trabajo
            // ya pagado a Groq) con este aviso. Se reporta por el mismo canal de log que ya usan los
            // demás pasos (log de actividad con hora en MainViewModel), sin agregar un mecanismo
            // nuevo solo para esto.
            if (!string.IsNullOrEmpty(result.TranslationWarning))
                onLog?.Report(result.TranslationWarning);
            return result.Text;
        }
    }

    /// <summary>
    /// Parsea la respuesta <c>{ text, id, translationWarning? }</c> del backend. Lógica pura (JSON ya
    /// leído en memoria, sin tocar red). Lanza <see cref="CloudTranscriptionException"/> si el JSON
    /// es inválido o no trae "text".
    /// </summary>
    public static CloudTranscriptionResult ParseResponse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CloudTranscriptionException("El servidor devolvió una respuesta inválida.", ex);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                throw new CloudTranscriptionException("El servidor no devolvió el texto transcripto.");

            var text = (textEl.GetString() ?? string.Empty).Trim();
            var id = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;
            var translationWarning =
                doc.RootElement.TryGetProperty("translationWarning", out var warnEl) && warnEl.ValueKind == JsonValueKind.String
                    ? warnEl.GetString()
                    : null;

            return new CloudTranscriptionResult(text, id, translationWarning);
        }
    }

    /// <summary>
    /// Mensaje amigable en español según el status HTTP: sesión vencida/sin sesión (401/403), audio
    /// muy grande (413 -- el backend corta en 25MB) y el resto como error genérico con el detalle
    /// del cuerpo (mismo criterio que <c>GroqTranscriptionService.TryExtractError</c>).
    /// </summary>
    public static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        var detail = TryExtractErrorDetail(body);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Tu sesión expiró o no es válida. Iniciá sesión de nuevo desde 'Sincronización'.",
            HttpStatusCode.RequestEntityTooLarge =>
                "El audio es demasiado grande para transcribir en la nube (máximo 25 MB). Probá con un archivo más chico o usá el motor Local.",
            _ => $"El servidor devolvió {(int)statusCode} {statusCode}: {detail}",
        };
    }

    private static string TryExtractErrorDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? json;
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? json;
        }
        catch { /* body no era JSON */ }
        return json;
    }
}

/// <summary>
/// Resultado parseado de la respuesta del backend (<c>{ text, id, translationWarning? }</c>).
/// <paramref name="TranslationWarning"/> solo viene poblado cuando se pidió traducir
/// (<see cref="CloudTranscriptionService.TranscribeAsync"/> con <c>translate: true</c>) y la
/// traducción falló -- la transcripción se guardó igual, sin traducir.
/// </summary>
public sealed record CloudTranscriptionResult(string Text, string? Id, string? TranslationWarning = null);

/// <summary>
/// Error de transcripción en la nube vía backend. El mensaje ya viene pensado para mostrarse
/// directo en la UI (ver <see cref="CloudTranscriptionService.BuildErrorMessage"/>).
/// </summary>
public sealed class CloudTranscriptionException : Exception
{
    public CloudTranscriptionException(string message) : base(message) { }
    public CloudTranscriptionException(string message, Exception inner) : base(message, inner) { }
}
