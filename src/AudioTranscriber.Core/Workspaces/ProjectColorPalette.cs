namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Paleta fija de 12 colores para "color por proyecto/carpeta" (estilo VS Code Peacock / Google
/// Drive), F2 en el sibling web (<c>audio-transcriber-web</c>). Única fuente de verdad de los ids
/// válidos: tanto la validación de persistencia (<see cref="Workspace.SaveProjectMeta"/> /
/// ReadMeta) como las claves de brush <c>Project{IdCapitalizado}</c> en
/// <c>Colors.Light.xaml</c>/<c>Colors.Dark.xaml</c> (ver <c>ProjectColorPaletteUiTests</c> en
/// AudioTranscriber.App.UiTests, que verifica que ambas fuentes queden en sync) derivan de esta
/// lista.
/// <para/>
/// Los ids en minúscula son lo que se persiste en disco (<c>_proyecto.json</c>) — deben ser
/// IDÉNTICOS a los ids del sibling web para consistencia cross-platform: no traducir ni renombrar.
/// <c>null</c> = "sin color" (neutral, el default, explícito para compatibilidad con
/// <c>_proyecto.json</c> viejos que no tienen el campo).
/// </summary>
public static class ProjectColorPalette
{
    /// <summary>Los 12 ids válidos, en el orden fijo del catálogo (mismo orden que el picker en la UI).</summary>
    public static readonly IReadOnlyList<string> Ids = new[]
    {
        "red", "orange", "amber", "green", "teal", "cyan",
        "blue", "indigo", "violet", "purple", "pink", "rose",
    };

    private static readonly IReadOnlySet<string> IdSet =
        new HashSet<string>(Ids, StringComparer.Ordinal);

    /// <summary>
    /// True si <paramref name="id"/> es uno de los 12 ids válidos. Comparación exacta
    /// (case-sensitive, <see cref="StringComparer.Ordinal"/>): los ids persistidos son siempre
    /// minúscula, así que no hace falta (ni conviene) normalizar mayúsculas acá.
    /// </summary>
    public static bool IsValid(string? id) => id is not null && IdSet.Contains(id);

    /// <summary>
    /// Valida <paramref name="id"/> contra la paleta: lo devuelve tal cual si es uno de los 12 ids
    /// válidos, o <c>null</c> si es <c>null</c>/vacío/desconocido (id de una versión futura de la
    /// app, JSON corrupto, etc.). Nunca tira — mismo criterio defensivo que
    /// <see cref="Workspace"/> usa para metadata corrupta.
    /// </summary>
    public static string? Normalize(string? id) => IsValid(id) ? id : null;
}
