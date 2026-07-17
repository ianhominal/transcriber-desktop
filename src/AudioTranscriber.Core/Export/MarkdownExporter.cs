using System.Text;

namespace AudioTranscriber.Core.Export;

/// <summary>Metadatos de una transcripción para el frontmatter del .md.</summary>
public sealed record TranscriptMetadata(
    DateTime Date,
    string AudioFile,
    string? Size,
    string Engine,
    string? Model);

/// <summary>
/// Genera notas Markdown con frontmatter YAML, pensadas para un vault de Obsidian
/// (o cualquier carpeta, p. ej. sincronizada con Google Drive).
/// </summary>
public static class MarkdownExporter
{
    /// <summary>
    /// Arma el contenido .md: frontmatter YAML + título + contexto opcional + el texto transcrito.
    /// </summary>
    public static string BuildMarkdown(TranscriptMetadata meta, string text, string? title, string? context)
    {
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var hasContext = !string.IsNullOrWhiteSpace(context);

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"fecha: {meta.Date:yyyy-MM-dd HH:mm}\n");
        if (hasTitle)
            sb.Append($"titulo: {title!.Trim()}\n");
        sb.Append($"audio: {meta.AudioFile}\n");
        if (!string.IsNullOrWhiteSpace(meta.Size))
            sb.Append($"tamaño: {meta.Size}\n");
        sb.Append($"motor: {meta.Engine}\n");
        if (!string.IsNullOrWhiteSpace(meta.Model))
            sb.Append($"modelo: {meta.Model}\n");
        sb.Append("tags: [transcripcion, audio]\n");
        sb.Append("---\n\n");

        if (hasTitle)
            sb.Append($"# {title!.Trim()}\n\n");

        if (hasContext)
        {
            sb.Append("## Contexto\n\n");
            sb.Append(context!.Trim());
            sb.Append("\n\n");
        }

        sb.Append("## Transcripción\n\n");
        sb.Append(text.Trim());
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Nombre del archivo .md. Usa el título si hay (sino el nombre del audio),
    /// con prefijo de fecha opcional (yyyy-MM-dd - nombre.md).
    /// </summary>
    public static string BuildFileName(string audioFile, DateTime date, bool includeDate, string? title)
    {
        var raw = !string.IsNullOrWhiteSpace(title)
            ? title!.Trim()
            : Path.GetFileNameWithoutExtension(audioFile);

        var baseName = Sanitize(raw);
        var prefix = includeDate ? $"{date:yyyy-MM-dd} - " : string.Empty;
        return $"{prefix}{baseName}.md";
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');
        return name.Trim();
    }
}
