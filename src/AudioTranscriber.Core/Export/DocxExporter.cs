using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AudioTranscriber.Core.Export;

/// <summary>
/// Genera un .docx real (formato OOXML de Word) con <see cref="NoteExportContent"/>, usando
/// <c>DocumentFormat.OpenXml</c> (paquete NuGet OFICIAL de Microsoft, gratis, sin dependencia de
/// Word instalado -- genera el XML/ZIP del formato directamente). Unicode (acentos/español) nativo:
/// <c>Text</c> guarda UTF-16 tal cual, sin escapes manuales.
/// </summary>
public static class DocxExporter
{
    public static void Build(string path, NoteExportContent content)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        if (!string.IsNullOrWhiteSpace(content.Title))
            body.AppendChild(StyledParagraph(content.Title.Trim(), bold: true, sizeHalfPoints: 32));

        body.AppendChild(StyledParagraph(content.Date.ToString("yyyy-MM-dd HH:mm"), italic: true, sizeHalfPoints: 18));

        body.AppendChild(StyledParagraph("Transcripción", bold: true, sizeHalfPoints: 28));
        AppendMultilineParagraphs(body, content.TranscriptText);

        if (!string.IsNullOrWhiteSpace(content.SummaryText))
        {
            body.AppendChild(StyledParagraph("Resumen", bold: true, sizeHalfPoints: 28));
            AppendMultilineParagraphs(body, content.SummaryText!);
        }

        if (content.KeyPoints.Count > 0)
        {
            body.AppendChild(StyledParagraph("Puntos clave", bold: true, sizeHalfPoints: 24));
            foreach (var point in content.KeyPoints)
                body.AppendChild(BulletParagraph(point));
        }

        if (content.ActionItems.Count > 0)
        {
            body.AppendChild(StyledParagraph("Tareas", bold: true, sizeHalfPoints: 24));
            foreach (var item in content.ActionItems)
                body.AppendChild(BulletParagraph(item, prefix: "[ ]  "));
        }

        mainPart.Document.Save();
    }

    private static Paragraph StyledParagraph(string text, int sizeHalfPoints, bool bold = false, bool italic = false)
    {
        var runProps = new RunProperties();
        if (bold) runProps.Append(new Bold());
        if (italic) runProps.Append(new Italic());
        runProps.Append(new FontSize { Val = sizeHalfPoints.ToString() });

        var run = new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "160", After = "120" }), run);
    }

    private static Paragraph BulletParagraph(string text, string prefix = "•  ") =>
        new(new Run(new Text(prefix + text) { Space = SpaceProcessingModeValues.Preserve }));

    /// <summary>
    /// Vuelca texto libre como uno o más <c>&lt;w:p&gt;</c>: bloques separados por línea en blanco
    /// se vuelven párrafos distintos; saltos de línea sueltos DENTRO de un bloque se vuelven
    /// <c>&lt;w:br/&gt;</c> (Word no interpreta "\n" crudo dentro de un <c>&lt;w:t&gt;</c>).
    /// </summary>
    private static void AppendMultilineParagraphs(Body body, string text)
    {
        var blocks = text.Replace("\r\n", "\n").Trim().Split("\n\n");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var runElements = new List<OpenXmlElement>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    runElements.Add(new Break());
                runElements.Add(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
            body.AppendChild(new Paragraph(new Run(runElements.ToArray())));
        }
    }
}
