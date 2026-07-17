using System.Text;

namespace AudioTranscriber.Core.Export;

/// <summary>
/// Arma el contenido .md/.txt de una <see cref="NoteExportContent"/> (exportar desde el detalle
/// nativo de la nota, <c>NoteDetailWindow</c>). Deliberadamente SEPARADO de
/// <see cref="MarkdownExporter"/> (usado por la exportación automática a Obsidian/Drive desde
/// <c>MainViewModel</c>): ese exporter pide <see cref="TranscriptMetadata"/> con datos del archivo
/// de audio (tamaño, motor, modelo) que el detalle de una nota no tiene a mano -- forzar ese shape
/// acá hubiera significado inventar valores falsos en el frontmatter. Lógica pura (solo arma
/// strings), testeable sin WPF.
/// </summary>
public static class NoteContentExporter
{
    public static string BuildMarkdown(NoteExportContent content)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(content.Title);

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"fecha: {content.Date:yyyy-MM-dd HH:mm}\n");
        if (hasTitle)
            sb.Append($"titulo: {content.Title.Trim()}\n");
        sb.Append("tags: [transcripcion, audio]\n");
        sb.Append("---\n\n");

        if (hasTitle)
            sb.Append($"# {content.Title.Trim()}\n\n");

        sb.Append("## Transcripción\n\n");
        sb.Append(content.TranscriptText.Trim());
        sb.Append('\n');

        AppendSummarySections(sb, content, markdown: true);
        return sb.ToString();
    }

    public static string BuildPlainText(NoteExportContent content)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(content.Title))
            sb.Append(content.Title.Trim()).Append('\n');
        sb.Append(content.Date.ToString("yyyy-MM-dd HH:mm")).Append("\n\n");

        sb.Append("Transcripción\n-------------\n");
        sb.Append(content.TranscriptText.Trim());
        sb.Append('\n');

        AppendSummarySections(sb, content, markdown: false);
        return sb.ToString();
    }

    private static void AppendSummarySections(StringBuilder sb, NoteExportContent content, bool markdown)
    {
        if (!string.IsNullOrWhiteSpace(content.SummaryText))
        {
            sb.Append(markdown ? "\n## Resumen\n\n" : "\nResumen\n-------\n");
            sb.Append(content.SummaryText!.Trim());
            sb.Append('\n');
        }

        if (content.KeyPoints.Count > 0)
        {
            sb.Append(markdown ? "\n### Puntos clave\n\n" : "\nPuntos clave:\n");
            foreach (var point in content.KeyPoints)
                sb.Append("- ").Append(point).Append('\n');
        }

        if (content.ActionItems.Count > 0)
        {
            sb.Append(markdown ? "\n### Tareas\n\n" : "\nTareas:\n");
            foreach (var item in content.ActionItems)
                sb.Append(markdown ? "- [ ] " : "- ").Append(item).Append('\n');
        }
    }

    /// <summary>
    /// Nombre de archivo sugerido: <c>yyyy-MM-dd - título.ext</c> (o "nota" si no hay título).
    /// Mismo criterio de sanitización que <see cref="MarkdownExporter.BuildFileName"/> (reemplaza
    /// caracteres inválidos de nombre de archivo por espacio) -- duplicado a propósito acá (la
    /// versión de MarkdownExporter es privada y ese exporter no aplica a este flujo, ver comentario
    /// de la clase) en vez de tocar código ya probado.
    /// </summary>
    public static string BuildFileName(string title, DateTime date, string extension)
    {
        var raw = string.IsNullOrWhiteSpace(title) ? "nota" : title.Trim();
        var sanitized = Sanitize(raw);
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return $"{date:yyyy-MM-dd} - {sanitized}{ext}";
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');
        return name.Trim();
    }
}
