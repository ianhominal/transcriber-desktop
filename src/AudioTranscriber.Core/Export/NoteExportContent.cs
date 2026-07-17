namespace AudioTranscriber.Core.Export;

/// <summary>
/// Contenido de una nota lista para exportar (.md/.txt/.docx/.pdf) desde el detalle nativo
/// (<c>NoteDetailWindow</c>) -- a diferencia de <see cref="TranscriptMetadata"/> (usado por
/// <see cref="MarkdownExporter"/> para la exportación automática desde <c>MainViewModel</c>, que
/// SÍ tiene metadata del archivo de audio: tamaño, motor, modelo), acá no hay audio asociado
/// directo -- solo lo que ya muestra el detalle de la nota: título, transcripción y,
/// opcionalmente, el resumen con IA si ya se generó.
/// </summary>
public sealed record NoteExportContent(
    string Title,
    DateTime Date,
    string TranscriptText,
    string? SummaryText,
    IReadOnlyList<string> KeyPoints,
    IReadOnlyList<string> ActionItems);
