using System;

namespace AudioTranscriber.Core.Diarization;

/// <summary>
/// Un tramo de audio atribuido a un hablante, tal como lo devuelve el diarizador (sherpa-onnx).
/// <paramref name="Speaker"/> es un id estable dentro de UN archivo (0, 1, 2…), no una identidad
/// real: el diarizador agrupa voces parecidas, no sabe cómo se llama nadie.
/// </summary>
public readonly record struct SpeakerSegment(TimeSpan Start, TimeSpan End, int Speaker);

/// <summary>Un segmento de transcripción ya atribuido a un hablante.</summary>
/// <param name="Speaker">Id del hablante, o <c>null</c> si no se pudo atribuir.</param>
public readonly record struct LabeledSegment(TimeSpan Start, TimeSpan End, string Text, int? Speaker);
