using AudioTranscriber.Core.Diarization;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Espejo de los tests de <c>splitSpeakerBlocks</c> de la web: el mismo texto tiene que parsear
/// igual en las dos apps (misma vista de hablantes en web y desktop).
/// </summary>
public class SpeakerTranscriptParserTests
{
    [Fact]
    public void Parse_splits_a_diarized_transcript_into_turns()
    {
        var text = "Persona 1: bueno, arranquemos.\n\nPersona 2: dale, te sigo.\n\nPersona 1: perfecto.";

        var blocks = SpeakerTranscriptParser.Parse(text);

        Assert.NotNull(blocks);
        Assert.Equal(3, blocks!.Count);
        Assert.Equal("Persona 1", blocks[0].Label);
        Assert.Equal("bueno, arranquemos.", blocks[0].Text);
        Assert.Equal("Persona 2", blocks[1].Label);
        Assert.Equal("dale, te sigo.", blocks[1].Text);
        Assert.Equal("Persona 1", blocks[2].Label);
    }

    [Fact]
    public void Parse_returns_null_for_a_plain_note()
    {
        // Una nota normal (o una transcripción grabada en la web, sin diarización) no tiene
        // etiquetas: no hay que inventarle turnos.
        Assert.Null(SpeakerTranscriptParser.Parse("Esto es una nota cualquiera.\n\nOtro párrafo sin etiquetas."));
    }

    [Fact]
    public void Parse_treats_an_unlabeled_chunk_after_a_turn_as_a_continuation()
    {
        // El pulido mete cortes de párrafo adentro de un turno largo; esos párrafos no llevan
        // etiqueta y son continuación del MISMO hablante, no un turno nuevo.
        var text = "Persona 1: primer párrafo.\n\nsegundo párrafo del mismo hablante.\n\nPersona 2: y yo respondo.";

        var blocks = SpeakerTranscriptParser.Parse(text);

        Assert.NotNull(blocks);
        Assert.Equal(2, blocks!.Count);
        Assert.Equal("Persona 1", blocks[0].Label);
        Assert.Equal("primer párrafo.\n\nsegundo párrafo del mismo hablante.", blocks[0].Text);
        Assert.Equal("Persona 2", blocks[1].Label);
    }

    [Fact]
    public void Parse_recognizes_the_unidentified_label()
    {
        var text = "Persona 1: hola.\n\nSin identificar: [algo inaudible]";

        var blocks = SpeakerTranscriptParser.Parse(text);

        Assert.NotNull(blocks);
        Assert.Equal(2, blocks!.Count);
        Assert.Equal(SpeakerTranscriptFormatter.UnknownSpeakerLabel, blocks[1].Label);
    }

    [Fact]
    public void Parse_does_not_open_a_turn_for_a_label_mentioned_mid_sentence()
    {
        // "Persona 2" en el medio de una frase NO abre un turno: el patrón ancla al inicio.
        var text = "Persona 1: le dije a la Persona 2 que viniera.";

        var blocks = SpeakerTranscriptParser.Parse(text);

        Assert.NotNull(blocks);
        Assert.Single(blocks!);
        Assert.Equal("le dije a la Persona 2 que viniera.", blocks![0].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_returns_null_for_empty_input(string? text)
    {
        Assert.Null(SpeakerTranscriptParser.Parse(text));
    }
}
