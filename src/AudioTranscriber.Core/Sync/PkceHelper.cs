using System.Security.Cryptography;
using System.Text;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Genera el par code_verifier / code_challenge para el flujo OAuth Authorization Code + PKCE
/// (RFC 7636), usado por <see cref="GoogleOAuthClient"/> para el login con Google via Supabase Auth.
/// </summary>
public static class PkceHelper
{
    // RFC 7636 sección 4.1: charset "unreserved" permitido en el code_verifier.
    private const string UnreservedChars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    /// <summary>
    /// Genera un code_verifier criptográficamente aleatorio (por defecto 128 caracteres, el
    /// máximo permitido por RFC 7636, que exige un rango de 43 a 128), usando únicamente el
    /// charset "unreserved" URL-safe del RFC. Usa rejection sampling para evitar el sesgo de
    /// módulo que introduciría un simple "byte % charsetSize".
    /// </summary>
    public static string GenerateCodeVerifier(int length = 128)
    {
        if (length is < 43 or > 128)
            throw new ArgumentOutOfRangeException(nameof(length), "El code_verifier debe tener entre 43 y 128 caracteres (RFC 7636).");

        var chars = new char[length];
        var filled = 0;

        // Mayor múltiplo de charsetSize que entra en un byte (0-255): descarta el resto para
        // que cada carácter del charset tenga exactamente la misma probabilidad.
        const int charsetSize = 66; // UnreservedChars.Length
        const int maxUnbiasedByte = (256 / charsetSize) * charsetSize; // 198

        Span<byte> buffer = stackalloc byte[128];
        while (filled < length)
        {
            RandomNumberGenerator.Fill(buffer);
            foreach (var b in buffer)
            {
                if (filled >= length) break;
                if (b >= maxUnbiasedByte) continue; // rechazado: evita sesgo de módulo
                chars[filled++] = UnreservedChars[b % charsetSize];
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Calcula el code_challenge = BASE64URL-ENCODE(SHA256(ASCII(code_verifier))), sin padding,
    /// tal como lo espera Supabase Auth al iniciar el flujo (query param code_challenge del
    /// endpoint /auth/v1/authorize, con code_challenge_method=s256).
    /// </summary>
    public static string CreateCodeChallenge(string codeVerifier)
    {
        var bytes = Encoding.ASCII.GetBytes(codeVerifier);
        var hash = SHA256.HashData(bytes);
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
