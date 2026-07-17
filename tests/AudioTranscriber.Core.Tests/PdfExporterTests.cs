using AudioTranscriber.Core.Export;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// QuestPDF no expone una API simple de extracción de texto para verificar contenido letra por
/// letra (a diferencia de OpenXml, ver DocxExporterTests) -- estos tests son de integración liviana:
/// generan el .pdf a un archivo temporal y verifican que sea un PDF válido (firma <c>%PDF</c>) con
/// contenido no trivial, para distintas formas de <see cref="NoteExportContent"/> (con/sin resumen,
/// con acentos). Sin UI ni audio, corre limpio con <c>dotnet test</c>.
/// </summary>
public class PdfExporterTests : IDisposable
{
    private readonly string _dir;

    public PdfExporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_pdf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Build_GeneraUnPdfValido()
    {
        var content = new NoteExportContent(
            "Reunión con el equipo", new DateTime(2026, 7, 14, 9, 30, 0),
            "Hola, ¿cómo estás? Nos vemos mañana.", null,
            Array.Empty<string>(), Array.Empty<string>());
        var path = Path.Combine(_dir, "nota.pdf");

        PdfExporter.Build(path, content);

        Assert.True(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 100);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public void Build_ConResumenYListas_NoLanza()
    {
        var content = new NoteExportContent(
            "Nota", new DateTime(2026, 7, 14, 9, 30, 0), "transcripción",
            "resumen corto",
            new[] { "punto clave uno", "punto clave dos" },
            new[] { "tarea uno" });
        var path = Path.Combine(_dir, "nota-completa.pdf");

        var ex = Record.Exception(() => PdfExporter.Build(path, content));

        Assert.Null(ex);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Build_SinTitulo_NoLanza()
    {
        var content = new NoteExportContent(
            "", new DateTime(2026, 7, 14, 9, 30, 0), "texto sin título", null,
            Array.Empty<string>(), Array.Empty<string>());
        var path = Path.Combine(_dir, "sin-titulo.pdf");

        var ex = Record.Exception(() => PdfExporter.Build(path, content));

        Assert.Null(ex);
    }
}
