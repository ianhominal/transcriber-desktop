using AudioTranscriber.Core.Export;

namespace AudioTranscriber.Core.Tests;

public class MarkdownExporterTests
{
    private static readonly DateTime SampleDate = new(2026, 7, 5, 18, 55, 0);

    [Fact]
    public void BuildMarkdown_includes_yaml_frontmatter_with_metadata()
    {
        var meta = new TranscriptMetadata(SampleDate, "nota.ogg", "338 KB", "groq", "whisper-large-v3-turbo");

        var md = MarkdownExporter.BuildMarkdown(meta, "Hola, ¿cómo estás?", title: null, context: null);

        Assert.StartsWith("---\n", md.Replace("\r\n", "\n"));
        Assert.Contains("fecha: 2026-07-05 18:55", md);
        Assert.Contains("audio: nota.ogg", md);
        Assert.Contains("tamaño: 338 KB", md);
        Assert.Contains("motor: groq", md);
        Assert.Contains("modelo: whisper-large-v3-turbo", md);
        Assert.Contains("tags: [transcripcion, audio]", md);
        Assert.Contains("Hola, ¿cómo estás?", md);
    }

    [Fact]
    public void BuildMarkdown_omits_model_when_null()
    {
        var meta = new TranscriptMetadata(SampleDate, "nota.mp3", null, "local", null);

        var md = MarkdownExporter.BuildMarkdown(meta, "texto", title: null, context: null);

        Assert.DoesNotContain("modelo:", md);
        Assert.DoesNotContain("tamaño:", md);
        Assert.Contains("motor: local", md);
    }

    [Fact]
    public void BuildMarkdown_includes_title_heading_and_context_section()
    {
        var meta = new TranscriptMetadata(SampleDate, "nota.ogg", null, "groq", "turbo");

        var md = MarkdownExporter.BuildMarkdown(
            meta, "el texto transcrito",
            title: "Reunión con el equipo",
            context: "Nota de voz de mi jefe sobre el proyecto X.");

        Assert.Contains("titulo: Reunión con el equipo", md);
        Assert.Contains("# Reunión con el equipo", md);
        Assert.Contains("## Contexto", md);
        Assert.Contains("Nota de voz de mi jefe sobre el proyecto X.", md);
        Assert.Contains("## Transcripción", md);
        Assert.Contains("el texto transcrito", md);
    }

    [Fact]
    public void BuildMarkdown_without_title_or_context_has_no_those_sections()
    {
        var meta = new TranscriptMetadata(SampleDate, "nota.ogg", null, "groq", "turbo");

        var md = MarkdownExporter.BuildMarkdown(meta, "texto", title: "", context: "  ").Replace("\r\n", "\n");

        Assert.DoesNotContain("titulo:", md);
        Assert.DoesNotContain("\n# ", md); // sin encabezado H1 de título
        Assert.DoesNotContain("## Contexto", md);
    }

    [Fact]
    public void BuildFileName_prefixes_date_and_uses_title_when_present()
    {
        var byAudio = MarkdownExporter.BuildFileName("nota.ogg", SampleDate, includeDate: true, title: null);
        var byTitle = MarkdownExporter.BuildFileName("nota.ogg", SampleDate, includeDate: true, title: "Mi Reunión");
        var noDate = MarkdownExporter.BuildFileName("nota.ogg", SampleDate, includeDate: false, title: null);

        Assert.Equal("2026-07-05 - nota.md", byAudio);
        Assert.Equal("2026-07-05 - Mi Reunión.md", byTitle);
        Assert.Equal("nota.md", noDate);
    }

    [Fact]
    public void BuildFileName_sanitizes_invalid_characters_in_title()
    {
        var name = MarkdownExporter.BuildFileName("a.ogg", SampleDate, includeDate: false, title: "Plan: fase 1/2");
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("/", name);
        Assert.EndsWith(".md", name);
    }
}
