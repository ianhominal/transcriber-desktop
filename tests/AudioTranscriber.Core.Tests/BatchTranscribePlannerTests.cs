using AudioTranscriber.Core.Workspaces;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class BatchTranscribePlannerTests
{
    [Fact]
    public void IsPending_AudioWithoutTranscript_IsPending()
    {
        var status = new BatchTranscribeAudioStatus(HasAudio: true, HasTranscript: false);

        Assert.True(BatchTranscribePlanner.IsPending(status));
    }

    [Fact]
    public void IsPending_AudioWithTranscript_IsNotPending()
    {
        var status = new BatchTranscribeAudioStatus(HasAudio: true, HasTranscript: true);

        Assert.False(BatchTranscribePlanner.IsPending(status));
    }

    /// Transcripción solo-texto (sin audio real, ver AudioItem.HasAudio): no hay nada para
    /// transcribir, aunque HasTranscript sea false (no debería pasar, pero por las dudas).
    [Fact]
    public void IsPending_TextOnlyEntry_IsNeverPending()
    {
        var status = new BatchTranscribeAudioStatus(HasAudio: false, HasTranscript: false);

        Assert.False(BatchTranscribePlanner.IsPending(status));
    }

    [Fact]
    public void CountPending_MixOfStatuses_CountsOnlyPendingWithRealAudio()
    {
        var statuses = new[]
        {
            new BatchTranscribeAudioStatus(HasAudio: true, HasTranscript: false),  // pendiente
            new BatchTranscribeAudioStatus(HasAudio: true, HasTranscript: true),   // ya transcripto
            new BatchTranscribeAudioStatus(HasAudio: true, HasTranscript: false),  // pendiente
            new BatchTranscribeAudioStatus(HasAudio: false, HasTranscript: false), // solo-texto
        };

        Assert.Equal(2, BatchTranscribePlanner.CountPending(statuses));
    }

    [Fact]
    public void CountPending_EmptyProject_IsZero()
    {
        Assert.Equal(0, BatchTranscribePlanner.CountPending(Array.Empty<BatchTranscribeAudioStatus>()));
    }

    [Fact]
    public void ConfirmMessage_IncludesCountAndProjectTitle()
    {
        var message = BatchTranscribePlanner.ConfirmMessage("Trabajo", 3);

        Assert.Equal("¿Transcribir los 3 audio(s) sin transcribir de 'Trabajo'?", message);
    }

    [Fact]
    public void ProgressMessage_ReportsCurrentOverTotal()
    {
        var message = BatchTranscribePlanner.ProgressMessage(2, 5);

        Assert.Equal("Transcribiendo 2 de 5…", message);
    }

    [Fact]
    public void SummaryMessage_NoFailures_ReportsAllTranscribed()
    {
        var message = BatchTranscribePlanner.SummaryMessage(total: 4, failed: 0);

        Assert.Equal("Listo: se transcribieron 4 audio(s).", message);
    }

    [Fact]
    public void SummaryMessage_WithFailures_ReportsSucceededAndFailed()
    {
        var message = BatchTranscribePlanner.SummaryMessage(total: 5, failed: 2);

        Assert.Equal("Listo: se transcribieron 3 de 5 audio(s) (2 con error).", message);
    }

    [Fact]
    public void CancelledMessage_ReportsHowManySucceededBeforeCancelling()
    {
        // Se intentaron 3 de 5, uno de esos 3 falló: quedaron 2 transcriptos antes de cancelar.
        var message = BatchTranscribePlanner.CancelledMessage(attempted: 3, total: 5, failed: 1);

        Assert.Equal("Transcripción del proyecto cancelada. Se transcribieron 2 de 5 audio(s).", message);
    }
}
