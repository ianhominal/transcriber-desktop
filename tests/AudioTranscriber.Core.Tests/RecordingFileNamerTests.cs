using AudioTranscriber.Core.Audio;

namespace AudioTranscriber.Core.Tests;

public class RecordingFileNamerTests
{
    [Fact]
    public void Generate_ArmaNombreConFechaYHoraSinCaracteresInvalidos()
    {
        var ts = new DateTime(2026, 7, 7, 14, 32, 5);
        var name = RecordingFileNamer.Generate(ts);

        Assert.Equal("Grabación 2026-07-07 14-32-05.wav", name);
        Assert.All(Path.GetInvalidFileNameChars(), c => Assert.DoesNotContain(c, name));
    }

    [Fact]
    public void Generate_EsDeterministico_MismaFechaMismoNombre()
    {
        var ts = new DateTime(2026, 1, 1, 0, 0, 0);
        Assert.Equal(RecordingFileNamer.Generate(ts), RecordingFileNamer.Generate(ts));
    }

    [Fact]
    public void Generate_FechasDistintas_NombresDistintos()
    {
        var a = RecordingFileNamer.Generate(new DateTime(2026, 1, 1, 10, 0, 0));
        var b = RecordingFileNamer.Generate(new DateTime(2026, 1, 1, 10, 0, 1));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GenerateForMeeting_ArmaNombreConFechaYHoraSinCaracteresInvalidos()
    {
        var ts = new DateTime(2026, 7, 7, 14, 32, 5);
        var name = RecordingFileNamer.GenerateForMeeting(ts);

        Assert.Equal("Reunión 2026-07-07 14-32-05.wav", name);
        Assert.All(Path.GetInvalidFileNameChars(), c => Assert.DoesNotContain(c, name));
    }

    /// Prefijo distinto a Generate: para poder distinguir de un vistazo una grabación de reunión
    /// (micrófono + audio del sistema) de una grabación normal (solo micrófono) en la lista de audios.
    [Fact]
    public void GenerateForMeeting_UsaPrefijoDistintoDeGenerate()
    {
        var ts = new DateTime(2026, 1, 1, 0, 0, 0);
        Assert.NotEqual(RecordingFileNamer.Generate(ts), RecordingFileNamer.GenerateForMeeting(ts));
    }
}
