using System.Text.Json;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Respuesta de <c>POST /api/audio/prepare</c>: el <see cref="Path"/> del objeto en Storage (keyeado
/// por un UUID), el <see cref="SignedUrl"/> para subir el audio comprimido DIRECTO a Storage
/// (salteando el body de la función de Vercel), y la <see cref="ApiKey"/> publishable/anon (pública)
/// que el gateway de Supabase exige en el header <c>apikey</c> del PUT. Ver <see cref="SyncEngine"/>
/// (UploadAudioAsync).
/// </summary>
public readonly record struct PreparedUpload(string Path, string SignedUrl, string ApiKey)
{
    /// <summary>
    /// Parsea el JSON <c>{ path, signedUrl, apiKey }</c> que devuelve el backend. Lanza
    /// <see cref="SyncApiException"/> (no <see cref="JsonException"/>) si el body no es JSON válido o
    /// le falta algún campo, para que el manejo de fallas del ciclo de sync lo trate como cualquier
    /// otra falla de red/backend (y no aborte el batch entero, ver SyncEngine.RunAsync).
    /// </summary>
    public static PreparedUpload Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new SyncApiException("La respuesta de preparar la subida no es JSON válido.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var isObj = root.ValueKind == JsonValueKind.Object;
            var path = isObj && root.TryGetProperty("path", out var p) ? p.GetString() : null;
            var signedUrl = isObj && root.TryGetProperty("signedUrl", out var s) ? s.GetString() : null;
            var apiKey = isObj && root.TryGetProperty("apiKey", out var k) ? k.GetString() : null;

            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(signedUrl) || string.IsNullOrEmpty(apiKey))
                throw new SyncApiException("La respuesta de preparar la subida no trajo path/signedUrl/apiKey.");

            return new PreparedUpload(path, signedUrl, apiKey);
        }
    }
}
