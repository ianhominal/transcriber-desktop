using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// Envoltorio de UI de un resultado de <c>GET /api/notes/search</c> (ver <see cref="AiSearchClient"/>):
/// agrega <see cref="DateText"/> ya formateado para la lista de resultados de <c>MainWindow</c>.
/// Inmutable a propósito (no observable): un resultado de búsqueda no cambia después de listarse.
/// </summary>
public sealed class AiSearchResultVm
{
    public string Id { get; }
    public string Title { get; }
    public string Snippet { get; }
    public string DateText { get; }

    public AiSearchResultVm(AiSearchResultDto dto)
    {
        Id = dto.Id;
        Title = dto.Title;
        Snippet = dto.Snippet;
        DateText = DateTime.TryParse(dto.CreatedAt, out var date)
            ? date.ToLocalTime().ToString("dd/MM/yyyy")
            : string.Empty;
    }
}
