namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Formateo puro de datos de perfil de usuario para la UI (avatar placeholder, etc.). Separado de
/// <see cref="AuthUser"/> para poder reusarlo desde SyncCoordinator (App), que solo guarda el
/// nombre ya resuelto (<see cref="AuthUser.DisplayName"/>) como string persistido en AppSettings,
/// sin mantener la instancia completa de <see cref="AuthUser"/> en memoria entre sesiones.
/// </summary>
public static class UserProfileFormatter
{
    /// <summary>
    /// Iniciales para el placeholder circular del avatar cuando no hay foto de perfil (o falla la
    /// carga). Toma la primera letra de hasta las dos primeras "palabras" de
    /// <paramref name="displayName"/> (separadas por espacio, mayúscula). "?" si viene vacío.
    /// </summary>
    public static string GetInitials(string? displayName)
    {
        var name = displayName?.Trim() ?? "";
        if (name.Length == 0)
            return "?";

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "?";

        var first = char.ToUpperInvariant(parts[0][0]);
        if (parts.Length == 1)
            return first.ToString();

        var second = char.ToUpperInvariant(parts[1][0]);
        return $"{first}{second}";
    }
}
