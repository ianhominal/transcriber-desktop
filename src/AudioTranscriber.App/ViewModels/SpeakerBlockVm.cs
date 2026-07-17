namespace AudioTranscriber.App.ViewModels;

/// <summary>
/// Un turno del transcript para la vista "Leer": si lleva etiqueta de hablante, el texto de la
/// etiqueta ya en mayúsculas para mostrar, el texto del turno, y el slot de color
/// (0..<see cref="Core.Diarization.SpeakerColorAssigner.PaletteSize"/>-1, o
/// <see cref="Core.Diarization.SpeakerColorAssigner.NeutralSlot"/>). Cuando no hay hablantes,
/// <see cref="HasLabel"/> es <c>false</c> y el bloque es el documento entero.
/// </summary>
public sealed record SpeakerBlockVm(bool HasLabel, string DisplayLabel, string Text, int ColorSlot);
