using AudioTranscriber.Core.Diarization;

namespace AudioTranscriber.Core.Tests;

/// <summary>Espejo de los tests de <c>assignSpeakerColors</c> de la web.</summary>
public class SpeakerColorAssignerTests
{
    [Fact]
    public void Assign_gives_each_speaker_a_distinct_slot_in_order()
    {
        var map = SpeakerColorAssigner.Assign(new[] { "Persona 1", "Persona 2", "Persona 3" });

        Assert.Equal(0, map["Persona 1"]);
        Assert.Equal(1, map["Persona 2"]);
        Assert.Equal(2, map["Persona 3"]);
    }

    [Fact]
    public void Assign_is_stable_when_a_speaker_talks_multiple_times()
    {
        var map = SpeakerColorAssigner.Assign(new[] { "Persona 1", "Persona 2", "Persona 1", "Persona 2", "Persona 1" });

        Assert.Equal(2, map.Count);
        Assert.Equal(0, map["Persona 1"]);
        Assert.Equal(1, map["Persona 2"]);
    }

    [Fact]
    public void Assign_sends_unidentified_to_neutral_without_spending_a_slot()
    {
        // "Sin identificar" aparece PRIMERO pero no debe robarle el slot 0 a Persona 1.
        var map = SpeakerColorAssigner.Assign(new[] { SpeakerTranscriptFormatter.UnknownSpeakerLabel, "Persona 1", "Persona 2" });

        Assert.Equal(SpeakerColorAssigner.NeutralSlot, map[SpeakerTranscriptFormatter.UnknownSpeakerLabel]);
        Assert.Equal(0, map["Persona 1"]);
        Assert.Equal(1, map["Persona 2"]);
    }

    [Fact]
    public void Assign_cycles_the_palette_when_there_are_more_speakers_than_colors()
    {
        var labels = Enumerable.Range(1, SpeakerColorAssigner.PaletteSize + 1)
            .Select(i => $"Persona {i}")
            .ToArray();

        var map = SpeakerColorAssigner.Assign(labels);

        // El que sigue al último color reusa el primero.
        Assert.Equal(map["Persona 1"], map[$"Persona {SpeakerColorAssigner.PaletteSize + 1}"]);
        Assert.NotEqual(map["Persona 1"], map[$"Persona {SpeakerColorAssigner.PaletteSize}"]);
    }

    [Fact]
    public void Assign_returns_empty_for_no_labels()
    {
        Assert.Empty(SpeakerColorAssigner.Assign(Array.Empty<string>()));
    }
}
