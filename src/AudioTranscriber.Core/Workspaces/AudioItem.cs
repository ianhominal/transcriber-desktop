namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// Un audio dentro del workspace, junto con el estado de su transcripción.
/// </summary>
public sealed record AudioItem
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required string TranscriptPath { get; init; }

    /// <summary>
    /// False para transcripciones SOLO TEXTO: un .txt en transcripts/ sin ningún archivo de audio
    /// correspondiente en audios/ (ver <see cref="Workspace.ListProjects"/>). Pasa cuando una
    /// transcripción bajada de la web nunca tuvo audio (<c>audio_url_signed</c> null/vacío en el
    /// pull — la subida del audio falló del lado del backend pero el texto sí se guardó). Default
    /// <c>true</c> para no romper ningún caller existente que construya <see cref="AudioItem"/>
    /// sin setear este campo explícitamente. Cuando es <c>false</c>, <see cref="FullPath"/> es
    /// <see cref="string.Empty"/> (no hay archivo real) — los callers que tocan el archivo de
    /// audio (reproducir, transcribir, mover, renombrar) deben chequear este flag primero.
    /// </summary>
    public bool HasAudio { get; init; } = true;

    /// <summary>True si ya existe el .txt correspondiente en /transcripts.</summary>
    public bool HasTranscript => File.Exists(TranscriptPath);
}
