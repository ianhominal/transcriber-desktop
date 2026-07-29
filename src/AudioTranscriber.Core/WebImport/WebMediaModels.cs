namespace AudioTranscriber.Core.WebImport;

/// <summary>
/// Un ítem de audio/video encontrado al analizar una URL con yt-dlp (sin descargar). <paramref
/// name="Index"/> es la posición 1-based dentro de la lista analizada (útil para pedirle a yt-dlp
/// ESE ítem puntual vía <c>--playlist-items</c> cuando <paramref name="Url"/> no vino resuelta a
/// una URL absoluta -- pasa en algunos extractores con <c>--flat-playlist</c>).
/// </summary>
public sealed record WebMediaItem(int Index, string Id, string Title, TimeSpan? Duration, string? Url);

/// <summary>
/// Resultado de analizar una página: si es una lista (playlist/álbum/canal) o un ítem suelto, y
/// los ítems encontrados. Cuando <paramref name="IsPlaylist"/> es false, <paramref name="Items"/>
/// siempre tiene exactamente un elemento.
/// </summary>
public sealed record WebMediaAnalysis(bool IsPlaylist, string? PlaylistTitle, IReadOnlyList<WebMediaItem> Items);
