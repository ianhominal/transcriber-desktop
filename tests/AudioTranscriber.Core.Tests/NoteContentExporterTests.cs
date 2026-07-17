using AudioTranscriber.Core.Export;

namespace AudioTranscriber.Core.Tests;

public class NoteContentExporterTests
{
    private static readonly DateTime SampleDate = new(2026, 7, 14, 9, 30, 0);

    private static NoteExportContent WithoutSummary(string title = "Reunión con el equipo") => new(
        title, SampleDate, "Hola, ¿cómo estás? Nos vemos mañana.", null,
        Array.Empty<string>(), Array.Empty<string>());

    private static NoteExportContent WithSummary() => new(
        "Reunión con el equipo", SampleDate, "el texto transcrito",
        "Resumen corto de la reunión.",
        new[] { "Punto uno", "Punto dos" },
        new[] { "Mandar el mail", "Agendar la próxima" });

    // ---- BuildMarkdown ---------------------------------------------------------------

    [Fact]
    public void BuildMarkdown_IncluyeFrontmatterConFechaYTitulo()
    {
        var md = NoteContentExporter.BuildMarkdown(WithoutSummary()).Replace("\r\n", "\n");

        Assert.StartsWith("---\n", md);
        Assert.Contains("fecha: 2026-07-14 09:30", md);
        Assert.Contains("titulo: Reunión con el equipo", md);
        Assert.Contains("# Reunión con el equipo", md);
        Assert.Contains("## Transcripción", md);
        Assert.Contains("Hola, ¿cómo estás? Nos vemos mañana.", md);
    }

    [Fact]
    public void BuildMarkdown_SinTitulo_OmiteFrontmatterYEncabezado()
    {
        var md = NoteContentExporter.BuildMarkdown(WithoutSummary(title: "")).Replace("\r\n", "\n");

        Assert.DoesNotContain("titulo:", md);
        Assert.DoesNotContain("\n# ", md);
    }

    [Fact]
    public void BuildMarkdown_ConResumen_AgregaSeccionesDeResumenPuntosYTareas()
    {
        var md = NoteContentExporter.BuildMarkdown(WithSummary());

        Assert.Contains("## Resumen", md);
        Assert.Contains("Resumen corto de la reunión.", md);
        Assert.Contains("### Puntos clave", md);
        Assert.Contains("- Punto uno", md);
        Assert.Contains("- Punto dos", md);
        Assert.Contains("### Tareas", md);
        Assert.Contains("- [ ] Mandar el mail", md);
        Assert.Contains("- [ ] Agendar la próxima", md);
    }

    [Fact]
    public void BuildMarkdown_SinResumen_NoAgregaEsasSecciones()
    {
        var md = NoteContentExporter.BuildMarkdown(WithoutSummary());

        Assert.DoesNotContain("## Resumen", md);
        Assert.DoesNotContain("### Puntos clave", md);
        Assert.DoesNotContain("### Tareas", md);
    }

    // ---- BuildPlainText --------------------------------------------------------------

    [Fact]
    public void BuildPlainText_IncluyeTituloFechaYTranscripcion()
    {
        var txt = NoteContentExporter.BuildPlainText(WithoutSummary());

        Assert.Contains("Reunión con el equipo", txt);
        Assert.Contains("2026-07-14 09:30", txt);
        Assert.Contains("Transcripción", txt);
        Assert.Contains("Hola, ¿cómo estás? Nos vemos mañana.", txt);
    }

    [Fact]
    public void BuildPlainText_ConResumen_IncluyeResumenPuntosYTareas()
    {
        var txt = NoteContentExporter.BuildPlainText(WithSummary());

        Assert.Contains("Resumen", txt);
        Assert.Contains("Resumen corto de la reunión.", txt);
        Assert.Contains("Puntos clave:", txt);
        Assert.Contains("- Punto uno", txt);
        Assert.Contains("Tareas:", txt);
        Assert.Contains("- Mandar el mail", txt);
    }

    // ---- BuildFileName -----------------------------------------------------------------

    [Fact]
    public void BuildFileName_UsaFechaYTitulo()
    {
        var name = NoteContentExporter.BuildFileName("Mi Reunión", SampleDate, ".md");
        Assert.Equal("2026-07-14 - Mi Reunión.md", name);
    }

    [Fact]
    public void BuildFileName_SinTitulo_UsaNota()
    {
        var name = NoteContentExporter.BuildFileName("", SampleDate, ".txt");
        Assert.Equal("2026-07-14 - nota.txt", name);
    }

    [Fact]
    public void BuildFileName_SanitizaCaracteresInvalidos()
    {
        var name = NoteContentExporter.BuildFileName("Plan: fase 1/2", SampleDate, ".pdf");
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("/", name);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public void BuildFileName_ExtensionSinPunto_AgregaElPunto()
    {
        var name = NoteContentExporter.BuildFileName("nota", SampleDate, "docx");
        Assert.EndsWith(".docx", name);
    }
}
