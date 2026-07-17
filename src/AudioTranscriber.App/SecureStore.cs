using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AudioTranscriber.App;

/// <summary>
/// Cifrado local de secretos con DPAPI (Windows Data Protection API).
/// El dato queda atado al usuario de Windows en esta máquina: solo ese usuario,
/// en esta PC, puede descifrarlo. Copiar el archivo a otra máquina no sirve.
/// </summary>
public static class SecureStore
{
    // Entropía adicional propia de la app (defensa en profundidad).
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AudioTranscriber.groq.v1");

    // ---- Secretos nombrados (sync con Supabase) --------------------------
    // Cada secreto se guarda en su propio archivo cifrado, uno por clave, para no mezclar
    // tokens de sesión con la config general (AppSettings). Nota histórica: AppSettings supo
    // tener un GroqApiKeyProtected (API key de Groq guardada en la PC) que se eliminó al pasar
    // el modo nube a transcribir vía backend (la key de Groq ahora vive solo server-side); el
    // nombre de Entropy de arriba quedó igual a propósito para no romper el descifrado de los
    // secretos ya guardados en máquinas existentes.

    /// <summary>Clave del access token de la sesión de Supabase.</summary>
    public const string SyncAccessTokenKey = "sync.accessToken";

    /// <summary>Clave del refresh token de la sesión de Supabase.</summary>
    public const string SyncRefreshTokenKey = "sync.refreshToken";

    /// <summary>Clave del vencimiento (unix seconds, campo "expires_at" de Supabase) del access token.</summary>
    public const string SyncExpiresAtKey = "sync.expiresAt";

    private static string SecretsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioTranscriber", "secrets");

    /// <summary>
    /// Cifra y persiste un secreto nombrado en su propio archivo. Un valor vacío/null borra
    /// el secreto existente (p.ej. al cerrar sesión).
    /// </summary>
    public static void SaveSecret(string keyName, string? value)
    {
        try
        {
            var path = SecretPathFor(keyName);
            if (string.IsNullOrEmpty(value))
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            Directory.CreateDirectory(SecretsDir);
            File.WriteAllText(path, Protect(value));
        }
        catch { /* si no se puede guardar, no es fatal */ }
    }

    /// <summary>Lee y descifra un secreto nombrado. "" si no existe o no se pudo descifrar.</summary>
    public static string LoadSecret(string keyName)
    {
        try
        {
            var path = SecretPathFor(keyName);
            return File.Exists(path) ? Unprotect(File.ReadAllText(path)) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Borra un secreto nombrado (p.ej. al cerrar sesión).</summary>
    public static void DeleteSecret(string keyName) => SaveSecret(keyName, null);

    private static string SecretPathFor(string keyName)
    {
        var safeName = string.Join("_", keyName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(SecretsDir, $"{safeName}.secret");
    }

    /// <summary>Cifra un texto y devuelve el resultado en base64 (o "" si es vacío).</summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var blob = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    /// <summary>Descifra un base64 producido por <see cref="Protect"/>. "" si falla o es de otra PC/usuario.</summary>
    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
            return string.Empty;

        try
        {
            var blob = Convert.FromBase64String(protectedBase64);
            var bytes = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty; // blob inválido, corrupto o de otro usuario/máquina
        }
    }
}
