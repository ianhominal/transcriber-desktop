using System.Text.Json;

namespace AudioTranscriber.Core.WebImport;

/// <summary>
/// Parsea la salida de <c>yt-dlp --dump-single-json --flat-playlist &lt;url&gt;</c> a
/// <see cref="WebMediaAnalysis"/>. Función pura sobre el string del JSON: nada de I/O, nada de
/// proceso -- por eso es la parte con más tests (es la que más se rompe cuando yt-dlp cambia el
/// shape de salida entre sitios/versiones).
/// <para/>
/// Con <c>--flat-playlist</c> yt-dlp NO resuelve cada entrada de una lista (no pega un request por
/// video): por eso las entradas de playlist suelen venir con menos campos que un ítem suelto
/// (típicamente sin <c>duration</c>). Este parser nunca revienta por un campo faltante: cae a un
/// fallback razonable (ver <see cref="ReadTitle"/>/<see cref="ReadDuration"/>/<see cref="ReadUrl"/>)
/// y solo tira <see cref="YtDlpParseException"/> cuando el JSON es inválido o no hay NINGÚN ítem
/// utilizable.
/// </summary>
public static class YtDlpJsonParser
{
    public const string NoAudioMessage = "No se encontró audio en esa página.";
    private const string InvalidJsonMessage = "La respuesta de yt-dlp no se pudo interpretar.";

    public static WebMediaAnalysis Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new YtDlpParseException(WebImportStatus.NoAudioFound, NoAudioMessage);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new YtDlpParseException(WebImportStatus.YtDlpFailed, InvalidJsonMessage);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new YtDlpParseException(WebImportStatus.NoAudioFound, NoAudioMessage);

            if (root.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
                return ParsePlaylist(root, entriesEl);

            return ParseSingle(root);
        }
    }

    private static WebMediaAnalysis ParsePlaylist(JsonElement root, JsonElement entriesEl)
    {
        var items = new List<WebMediaItem>();
        var index = 0;
        foreach (var entry in entriesEl.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            index++;
            items.Add(ParseItem(entry, index));
        }

        if (items.Count == 0)
            throw new YtDlpParseException(WebImportStatus.NoAudioFound, NoAudioMessage);

        var playlistTitle = ReadString(root, "title");
        return new WebMediaAnalysis(IsPlaylist: true, PlaylistTitle: playlistTitle, Items: items);
    }

    private static WebMediaAnalysis ParseSingle(JsonElement root)
    {
        var item = ParseItem(root, index: 1);
        return new WebMediaAnalysis(IsPlaylist: false, PlaylistTitle: null, Items: new[] { item });
    }

    private static WebMediaItem ParseItem(JsonElement element, int index)
    {
        var id = ReadString(element, "id");
        var url = ReadUrl(element);

        // Sin id, sin título Y sin url: no hay nada con qué identificar este ítem -- lo tratamos
        // como "no encontrado" en vez de devolver un item fantasma que después no se puede ni
        // mostrar ni descargar.
        var title = ReadString(element, "title");
        if (id is null && title is null && url is null)
            throw new YtDlpParseException(WebImportStatus.NoAudioFound, NoAudioMessage);

        var resolvedId = id ?? $"item-{index}";
        var resolvedTitle = title ?? resolvedId;
        var duration = ReadDuration(element);

        return new WebMediaItem(index, resolvedId, resolvedTitle, duration, url);
    }

    /// <summary>
    /// URL del ítem: preferimos <c>webpage_url</c> (la URL "humana" de la página, la que después
    /// sirve para volver a pedirle a yt-dlp ESE contenido) y caemos a <c>url</c> solo si es una URL
    /// absoluta -- en <c>--flat-playlist</c> algunos extractores dejan en <c>url</c> apenas un id
    /// interno, no una URL real, y usar eso tal cual rompería la descarga.
    /// </summary>
    private static string? ReadUrl(JsonElement element)
    {
        var webpageUrl = ReadString(element, "webpage_url");
        if (webpageUrl is not null && LooksAbsolute(webpageUrl))
            return webpageUrl;

        var url = ReadString(element, "url");
        if (url is not null && LooksAbsolute(url))
            return url;

        return null;
    }

    private static bool LooksAbsolute(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static TimeSpan? ReadDuration(JsonElement element)
    {
        if (!element.TryGetProperty("duration", out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetDouble(out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}

/// <summary>
/// Falla al parsear la salida de yt-dlp. <see cref="Status"/> ya viene resuelto al
/// <see cref="WebImportStatus"/> que corresponde mostrarle a la usuaria -- <see cref="WebPageAnalyzer"/>
/// solo lo reenvía, no tiene que reinterpretar el mensaje.
/// </summary>
public sealed class YtDlpParseException : Exception
{
    public WebImportStatus Status { get; }

    public YtDlpParseException(WebImportStatus status, string message) : base(message)
    {
        Status = status;
    }
}
