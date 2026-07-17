using System.Security.Cryptography;
using System.Text;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Hash estable (SHA-256, hex) usado por <see cref="LocalScanner"/> y <see cref="RemoteMapper"/>
/// para detectar cambios de contenido entre syncs. Determinístico: mismas partes -&gt; mismo hash.
/// </summary>
internal static class ContentHasher
{
    private const char Separator = '';

    public static string Hash(params string?[] parts)
    {
        var joined = string.Join(Separator, parts.Select(p => p ?? string.Empty));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
