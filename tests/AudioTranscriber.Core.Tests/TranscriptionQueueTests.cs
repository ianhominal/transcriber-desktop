using AudioTranscriber.Core.Workspaces;
using Xunit;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Cola general de transcripción (refactor de concurrencia, brief "grabar mientras se transcribe +
/// cola de transcripción"): tocar Transcribir sobre otro audio mientras uno ya corre se ENCOLA en
/// vez de bloquear el botón, y se procesan de a uno. Genérica en TKey/TItem para no atarse a
/// AudioItemVm (App, WPF) -- estos tests usan string como key (misma idea que el ViewModel: la
/// ruta completa del audio) y también un tipo cualquiera como item, para dejar en claro que la
/// cola no le pide nada al item más que existir.
/// </summary>
public class TranscriptionQueueTests
{
    [Fact]
    public void Enqueue_NewKey_ReturnsTrueAndCountsOne()
    {
        var queue = new TranscriptionQueue<string, string>();

        var added = queue.Enqueue("audio1.mp3", "audio1.mp3");

        Assert.True(added);
        Assert.Equal(1, queue.Count);
        Assert.False(queue.IsEmpty);
    }

    [Fact]
    public void Enqueue_DuplicateKey_ReturnsFalseAndDoesNotDuplicate()
    {
        var queue = new TranscriptionQueue<string, string>();
        queue.Enqueue("audio1.mp3", "audio1.mp3");

        var addedAgain = queue.Enqueue("audio1.mp3", "audio1.mp3");

        Assert.False(addedAgain);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Dequeue_ReturnsItemsInFifoOrder()
    {
        var queue = new TranscriptionQueue<string, string>();
        queue.Enqueue("a.mp3", "a.mp3");
        queue.Enqueue("b.mp3", "b.mp3");
        queue.Enqueue("c.mp3", "c.mp3");

        Assert.Equal("a.mp3", queue.Dequeue());
        Assert.Equal("b.mp3", queue.Dequeue());
        Assert.Equal("c.mp3", queue.Dequeue());
    }

    [Fact]
    public void Dequeue_EmptyQueue_ReturnsDefault()
    {
        var queue = new TranscriptionQueue<string, string>();

        var result = queue.Dequeue();

        Assert.Null(result);
    }

    [Fact]
    public void Dequeue_RemovesFromQueue_CountDecreases()
    {
        var queue = new TranscriptionQueue<string, string>();
        queue.Enqueue("a.mp3", "a.mp3");
        queue.Enqueue("b.mp3", "b.mp3");

        queue.Dequeue();

        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void IsQueued_QueuedKey_IsTrue()
    {
        var queue = new TranscriptionQueue<string, string>();
        queue.Enqueue("a.mp3", "a.mp3");

        Assert.True(queue.IsQueued("a.mp3"));
    }

    [Fact]
    public void IsQueued_NeverEnqueuedKey_IsFalse()
    {
        var queue = new TranscriptionQueue<string, string>();

        Assert.False(queue.IsQueued("nunca-encolado.mp3"));
    }

    [Fact]
    public void IsQueued_AfterDequeue_IsFalse()
    {
        var queue = new TranscriptionQueue<string, string>();
        queue.Enqueue("a.mp3", "a.mp3");

        queue.Dequeue();

        Assert.False(queue.IsQueued("a.mp3"));
    }

    [Fact]
    public void Enqueue_SameKeyAfterDequeue_CanBeReQueued()
    {
        // El dedupe solo aplica MIENTRAS está encolado -- un audio ya procesado (o sacado de la
        // cola por otro motivo) tiene que poder volver a encolarse (re-transcribir).
        var queue = new TranscriptionQueue<string, string>();
        queue.Enqueue("a.mp3", "a.mp3");
        queue.Dequeue();

        var added = queue.Enqueue("a.mp3", "a.mp3");

        Assert.True(added);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Clear_EmptiesQueue()
    {
        var queue = new TranscriptionQueue<string, string>();
        queue.Enqueue("a.mp3", "a.mp3");
        queue.Enqueue("b.mp3", "b.mp3");

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.True(queue.IsEmpty);
        Assert.False(queue.IsQueued("a.mp3"));
        Assert.False(queue.IsQueued("b.mp3"));
    }

    [Fact]
    public void Clear_ThenEnqueue_WorksNormally()
    {
        // Vaciar no debe dejar la cola en un estado raro -- Cancelar (MainViewModel) vacía y la
        // usuaria tiene que poder volver a encolar después sin reiniciar la app.
        var queue = new TranscriptionQueue<string, string>();
        queue.Enqueue("a.mp3", "a.mp3");
        queue.Clear();

        var added = queue.Enqueue("b.mp3", "b.mp3");

        Assert.True(added);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Enqueue_DistinguishesKeyFromItem_ItemCanCarryRicherData()
    {
        // Mismo caso de uso real que MainViewModel: TKey es la ruta (para dedupe/¿está encolado?),
        // TItem es el objeto real que se necesita al procesar (en la app, AudioItemVm).
        var queue = new TranscriptionQueue<string, int>();
        queue.Enqueue("audio1.mp3", 42);

        Assert.Equal(42, queue.Dequeue());
    }
}
