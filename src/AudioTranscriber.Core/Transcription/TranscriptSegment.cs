namespace AudioTranscriber.Core.Transcription;

/// <summary>Un fragmento transcrito, con sus marcas de tiempo dentro del audio.</summary>
public readonly record struct TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);
