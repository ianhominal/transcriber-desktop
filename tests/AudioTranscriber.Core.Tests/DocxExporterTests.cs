using AudioTranscriber.Core.Export;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Round-trip: genera el .docx a un archivo temporal y lo vuelve a abrir con el SDK de OpenXml
/// para verificar que el contenido esperado está presente -- no hay forma de testear "lógica pura"
/// acá (el output es un ZIP/XML binario), así que esto es un test de integración liviano, sin UI ni
/// audio, se puede correr con <c>dotnet test</c> sin generar ruido.
/// </summary>
public class DocxExporterTests : IDisposable
{
    private readonly string _dir;

    public DocxExporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "at_docx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static string BodyText(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return doc.MainDocumentPart!.Document!.Body!.InnerText;
    }

    [Fact]
    public void Build_GeneraUnArchivoDocxValido()
    {
        var content = new NoteExportContent(
            "Reunión con el equipo", new DateTime(2026, 7, 14, 9, 30, 0),
            "Hola, ¿cómo estás?", null, Array.Empty<string>(), Array.Empty<string>());
        var path = Path.Combine(_dir, "nota.docx");

        DocxExporter.Build(path, content);

        Assert.True(File.Exists(path));
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.NotNull(doc.MainDocumentPart?.Document?.Body);
    }

    [Fact]
    public void Build_IncluyeTituloYTranscripcion()
    {
        var content = new NoteExportContent(
            "Reunión con el equipo", new DateTime(2026, 7, 14, 9, 30, 0),
            "El texto transcripto, con acentos: reunión, mañana.", null,
            Array.Empty<string>(), Array.Empty<string>());
        var path = Path.Combine(_dir, "nota.docx");

        DocxExporter.Build(path, content);

        var text = BodyText(path);
        Assert.Contains("Reunión con el equipo", text);
        Assert.Contains("Transcripción", text);
        Assert.Contains("El texto transcripto, con acentos: reunión, mañana.", text);
    }

    [Fact]
    public void Build_ConResumenYListas_IncluyeTodasLasSecciones()
    {
        var content = new NoteExportContent(
            "Nota", new DateTime(2026, 7, 14, 9, 30, 0), "transcripción",
            "resumen corto",
            new[] { "punto clave uno" },
            new[] { "tarea uno" });
        var path = Path.Combine(_dir, "nota.docx");

        DocxExporter.Build(path, content);

        var text = BodyText(path);
        Assert.Contains("Resumen", text);
        Assert.Contains("resumen corto", text);
        Assert.Contains("Puntos clave", text);
        Assert.Contains("punto clave uno", text);
        Assert.Contains("Tareas", text);
        Assert.Contains("tarea uno", text);
    }

    [Fact]
    public void Build_SinTitulo_NoRompeYSigueTeniendoTranscripcion()
    {
        var content = new NoteExportContent(
            "", new DateTime(2026, 7, 14, 9, 30, 0), "texto sin título", null,
            Array.Empty<string>(), Array.Empty<string>());
        var path = Path.Combine(_dir, "nota.docx");

        DocxExporter.Build(path, content);

        Assert.Contains("texto sin título", BodyText(path));
    }
}
