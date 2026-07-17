using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AudioTranscriber.Core.Export;

/// <summary>
/// Genera un .pdf con <see cref="NoteExportContent"/> usando QuestPDF -- licencia Community
/// (gratis para individuos/empresas chicas, caso de este proyecto indie, ver
/// https://www.questpdf.com/license/). Se eligió QuestPDF sobre PdfSharp: su API fluida de layout
/// (columnas + wrap de texto automático) resuelve párrafos largos y acentuados sin tener que
/// medir/posicionar glyphs a mano (PdfSharp por sí solo no hace wrap de texto; necesitaría además
/// MigraDoc para ese nivel, más piezas moviéndose por el mismo resultado). Unicode (español) nativo,
/// sin configuración extra.
/// </summary>
public static class PdfExporter
{
    static PdfExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void Build(string path, NoteExportContent content)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(11));

                page.Header().Column(col =>
                {
                    if (!string.IsNullOrWhiteSpace(content.Title))
                        col.Item().Text(content.Title.Trim()).FontSize(18).Bold();
                    col.Item().Text(content.Date.ToString("yyyy-MM-dd HH:mm")).FontSize(9).Italic();
                });

                page.Content().PaddingTop(12).Column(col =>
                {
                    col.Spacing(10);

                    col.Item().Text("Transcripción").FontSize(14).Bold();
                    col.Item().Text(content.TranscriptText.Trim());

                    if (!string.IsNullOrWhiteSpace(content.SummaryText))
                    {
                        col.Item().Text("Resumen").FontSize(14).Bold();
                        col.Item().Text(content.SummaryText!.Trim());
                    }

                    if (content.KeyPoints.Count > 0)
                    {
                        col.Item().Text("Puntos clave").FontSize(13).Bold();
                        foreach (var point in content.KeyPoints)
                            col.Item().Text($"•  {point}");
                    }

                    if (content.ActionItems.Count > 0)
                    {
                        col.Item().Text("Tareas").FontSize(13).Bold();
                        foreach (var item in content.ActionItems)
                            col.Item().Text($"[ ]  {item}");
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        });

        document.GeneratePdf(path);
    }
}
