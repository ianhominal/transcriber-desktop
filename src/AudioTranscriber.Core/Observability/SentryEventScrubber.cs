using System.Text.RegularExpressions;

namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Filtro de PII para eventos de Sentry: lógica pura, SIN dependencia del SDK de Sentry (que vive
/// solo en AudioTranscriber.App — Core no referencia el paquete Sentry). Opera sobre texto y
/// diccionarios string→string para poder testearse sin instanciar tipos reales de Sentry
/// (SentryEvent/Breadcrumb); el wiring real (convertir un SentryEvent/Breadcrumb hacia/desde estos
/// primitivos dentro de los callbacks BeforeSend/BeforeBreadcrumb) vive en
/// <c>AudioTranscriber.App.SentryPiiFilter</c>.
/// <para/>
/// La app maneja tokens de sesión de Google/Supabase (ver <c>SecureStore</c> en App) y esos valores
/// NUNCA deben viajar a Sentry: ni como valor de un campo con nombre sensible (Extra/Tag), ni
/// incrustados en texto libre (mensaje de excepción, breadcrumb) donde alguien los haya concatenado
/// a mano (p.ej. un header "Authorization: Bearer ...").
/// </summary>
public static class SentryEventScrubber
{
    private const string Redacted = "[scrubbed]";

    /// <summary>Fragmentos de nombre de clave (Extra/Tag) que marcan un dato sensible sea cual sea su valor.</summary>
    private static readonly string[] SensitiveKeyFragments =
    [
        "token", "secret", "password", "apikey", "api_key", "authorization", "credential", "cookie",
    ];

    // JWT-like: tres segmentos base64url separados por ".", el primero empieza con "ey" (header
    // JSON codificado, patrón de todo JWT real: {"alg":...} o {"typ":...}).
    private static readonly Regex JwtPattern = new(
        @"\bey[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled);

    private static readonly Regex BearerPattern = new(
        @"Bearer\s+\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Solo oculta el segmento del nombre de usuario (C:\Users\<nombre>\...), el resto de la ruta
    // queda legible para diagnóstico.
    private static readonly Regex UserPathPattern = new(
        @"([A-Za-z]:\\Users\\)([^\\""]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True si el nombre de clave sugiere un dato sensible (token, password, credencial, etc.).</summary>
    public static bool IsSensitiveKey(string key) =>
        SensitiveKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Scrubbea texto libre: reemplaza tokens tipo JWT, headers "Bearer ..." y el nombre de usuario
    /// en rutas de Windows por un placeholder, dejando el resto del mensaje legible para diagnóstico.
    /// </summary>
    public static string? ScrubText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = JwtPattern.Replace(text, Redacted);
        result = BearerPattern.Replace(result, $"Bearer {Redacted}");
        result = UserPathPattern.Replace(result, $"$1{Redacted}");
        return result;
    }

    /// <summary>
    /// Scrubbea un único par clave/valor: si la clave es sensible (<see cref="IsSensitiveKey"/>) el
    /// valor entero se reemplaza sin importar su contenido; si no, igual se le aplica
    /// <see cref="ScrubText"/> por si el valor trae texto libre con un token incrustado.
    /// </summary>
    public static string? ScrubValueForKey(string key, string? value) =>
        IsSensitiveKey(key) ? Redacted : ScrubText(value);

    /// <summary>Aplica <see cref="ScrubValueForKey"/> a cada entrada de un diccionario, en una copia nueva.</summary>
    public static Dictionary<string, string?> ScrubDictionary(IReadOnlyDictionary<string, string?> data)
    {
        var scrubbed = new Dictionary<string, string?>(data.Count);
        foreach (var (key, value) in data)
            scrubbed[key] = ScrubValueForKey(key, value);
        return scrubbed;
    }
}
