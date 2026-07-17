namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Un proyecto = una subcarpeta dentro de <c>audios/</c> que agrupa audios.
/// El proyecto "General" (<see cref="IsGeneral"/>) son los audios sueltos en la raíz de audios/.
/// </summary>
public sealed class AudioProject
{
    /// <summary>Nombre de la carpeta (o "General" para los audios sueltos).</summary>
    public required string Name { get; init; }

    /// <summary>Ruta de la carpeta del proyecto (audios/ para General, audios/Nombre para el resto).</summary>
    public required string FolderPath { get; init; }

    /// <summary>True si son los audios sueltos en la raíz de audios/.</summary>
    public required bool IsGeneral { get; init; }

    /// <summary>Título editable (metadata). Por defecto, el nombre de la carpeta.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Descripción/notas del proyecto (metadata).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Color de acento del proyecto (metadata): uno de los ids de <see cref="ProjectColorPalette"/>,
    /// o <c>null</c> = "sin color" (neutral, el default). El proyecto "General" (<see cref="IsGeneral"/>)
    /// nunca tiene color (no tiene <c>_proyecto.json</c>).
    /// </summary>
    public string? Color { get; set; }

    /// <summary>Audios del proyecto.</summary>
    public required IReadOnlyList<AudioItem> Audios { get; init; }
}
