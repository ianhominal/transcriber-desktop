using AudioTranscriber.Core.Diarization;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Cubre <see cref="DiarizationService.MapSegment"/>: la única lógica PURA de DiarizationService
/// (traducir el resultado crudo de sherpa-onnx -- segundos en float, id de hablante -- a nuestro
/// SpeakerSegment). El resto de la clase (Run) necesita un modelo real cargado y no se testea acá
/// -- ver el pedido explícito de no testear la inferencia del modelo.
/// </summary>
public class DiarizationServiceMappingTests
{
    [Fact]
    public void MapSegment_converts_float_seconds_to_TimeSpan()
    {
        var seg = DiarizationService.MapSegment(1.5f, 3.25f, speaker: 0);

        Assert.Equal(TimeSpan.FromSeconds(1.5), seg.Start);
        Assert.Equal(TimeSpan.FromSeconds(3.25), seg.End);
        Assert.Equal(0, seg.Speaker);
    }

    [Fact]
    public void MapSegment_preserves_the_speaker_id_as_is()
    {
        // sherpa-onnx numera hablantes desde 0 -- acá NO se le suma 1 ni se traduce a "Persona N":
        // eso es trabajo de SpeakerTranscriptFormatter, más adelante en el pipeline.
        var seg = DiarizationService.MapSegment(0f, 1f, speaker: 3);

        Assert.Equal(3, seg.Speaker);
    }

    [Fact]
    public void MapSegment_at_zero_seconds_maps_to_TimeSpan_zero()
    {
        var seg = DiarizationService.MapSegment(0f, 0f, speaker: 0);

        Assert.Equal(TimeSpan.Zero, seg.Start);
        Assert.Equal(TimeSpan.Zero, seg.End);
    }
}
