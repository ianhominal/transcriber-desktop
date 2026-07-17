using Whisper.net.Ggml;

namespace AudioTranscriber.Core.Transcription;

/// <summary>
/// Catálogo de modelos GGML de Whisper seleccionables para el motor Local. Hasta el 2026-07-15 el
/// motor Local usaba SIEMPRE <see cref="GgmlType.Small"/> (167 MB, el modelo más chico de Whisper,
/// encima cuantizado Q5_0) sin ninguna forma de elegir otro -- eso explicaba transcripciones en
/// español con errores reales reportados por un usuario ("Acompanianos" sin ñ, "Esplota",
/// "TaySports"). Este catálogo le da al motor Local la misma libertad de elegir calidad que ya
/// tenía la nube (ver <c>AppSettings.GroqModel</c>), con el mismo criterio de validación que
/// <see cref="TranslationOptions.ResolveLanguage"/>: allowlist estricta, cualquier valor
/// desconocido/corrupto cae a un default fijo.
///
/// Los tres modelos siguen cuantizados Q5_0 (mismo criterio que el único modelo fijo de antes,
/// ver <see cref="WhisperModelProvider"/>): buen balance calidad/tamaño para CPU modestas. Tamaños
/// reales verificados con HEAD a Hugging Face (2026-07-15, carpeta "q5_0" del repo
/// sandrohanea/whisper.net): small 167 MB, medium 514 MB, large-v3 1031 MB.
/// </summary>
public static class LocalModelOptions
{
    /// <param name="Id">Valor persistido en <c>AppSettings.LocalModelId</c> y usado como
    /// <c>Tag</c> del combo en MainWindow.xaml.</param>
    /// <param name="Label">Texto que ve la usuaria: SIN "small/medium/large", SIN "q5_0", SIN
    /// "cuantizado" -- solo el trade-off (velocidad/calidad) y el tamaño real de la descarga.</param>
    /// <param name="Type">Tipo que le pide Whisper.net al <see cref="WhisperModelProvider"/>.</param>
    public sealed record Model(string Id, string Label, GgmlType Type);

    public static readonly IReadOnlyList<Model> Models = new[]
    {
        new Model("small", "Rápido (167 MB)", GgmlType.Small),
        new Model("medium", "Bueno (514 MB)", GgmlType.Medium),
        new Model("large-v3", "El mejor (1 GB)", GgmlType.LargeV3),
    };

    /// <summary>
    /// "small" por default -- NO CAMBIAR: es el único modelo que existía antes de este selector, así
    /// que subir el default forzaría una descarga de cientos de MB no pedida a quien ya viene
    /// usando la app.
    /// </summary>
    public const string DefaultModelId = "small";

    private static readonly IReadOnlyDictionary<string, Model> ById =
        Models.ToDictionary(m => m.Id, StringComparer.Ordinal);

    /// <summary>
    /// Valida el id pedido (típicamente <c>AppSettings.LocalModelId</c>) contra la allowlist de
    /// <see cref="Models"/>; cualquier valor nulo, vacío o desconocido cae al default (mismo
    /// criterio que <see cref="TranslationOptions.ResolveLanguage"/>). Nunca lanza.
    /// </summary>
    public static Model Resolve(string? requestedId) =>
        requestedId is not null && ById.TryGetValue(requestedId, out var model) ? model : ById[DefaultModelId];

    /// <summary>
    /// Nombre real del archivo remoto en Hugging Face para cada <see cref="GgmlType"/> (sin el
    /// prefijo "ggml-" ni la extensión ".bin" -- ver <see cref="WhisperModelProvider"/>, que arma
    /// la URL completa con esto). Mapeo EXPLÍCITO a propósito: <c>GgmlType.LargeV3.ToString()
    /// .ToLowerInvariant()</c> da "largev3" (sin guión), pero el archivo real es
    /// "ggml-large-v3.bin" (CON guión) -- 404 garantizado. No pasaba desapercibido porque hasta
    /// ahora solo se usaba Small, cuyo nombre coincide con <c>ToString()</c> por pura casualidad.
    /// </summary>
    public static string RemoteFileName(GgmlType type) => type switch
    {
        GgmlType.Tiny => "tiny",
        GgmlType.TinyEn => "tiny.en",
        GgmlType.Base => "base",
        GgmlType.BaseEn => "base.en",
        GgmlType.Small => "small",
        GgmlType.SmallEn => "small.en",
        GgmlType.Medium => "medium",
        GgmlType.MediumEn => "medium.en",
        GgmlType.LargeV1 => "large-v1",
        GgmlType.LargeV2 => "large-v2",
        GgmlType.LargeV3 => "large-v3",
        GgmlType.LargeV3Turbo => "large-v3-turbo",
        _ => throw new ArgumentOutOfRangeException(
            nameof(type), type, "Tipo de modelo sin nombre de archivo remoto mapeado -- agregalo acá antes de usarlo (no asumas que coincide con ToString())."),
    };
}
