using AudioTranscriber.Core.Audio;
using Xunit;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Solo lo determinístico y sin dispositivos: estado inicial y que Stop()/Dispose() sin haber
/// arrancado nunca no rompan nada. Arrancar de verdad requiere micrófono/placa de sonido reales,
/// eso no se testea acá (mismo criterio que MicrophoneRecorder, sin tests de captura real).
/// </summary>
public class MeetingRecorderTests
{
    [Fact]
    public void RecordingFormat_EsElMismoQueMicrophoneRecorder()
    {
        // Para que el WAV de una reunión y el de una grabación normal sean intercambiables (mismo
        // target de Whisper): un solo formato, reusado, no dos definiciones que puedan divergir.
        Assert.Equal(MicrophoneRecorder.RecordingFormat.SampleRate, MeetingRecorder.RecordingFormat.SampleRate);
        Assert.Equal(MicrophoneRecorder.RecordingFormat.BitsPerSample, MeetingRecorder.RecordingFormat.BitsPerSample);
        Assert.Equal(MicrophoneRecorder.RecordingFormat.Channels, MeetingRecorder.RecordingFormat.Channels);
    }

    [Fact]
    public void EstadoInicial_NoEstaGrabando()
    {
        using var recorder = new MeetingRecorder();

        Assert.False(recorder.IsRecording);
        Assert.Null(recorder.OutputPath);
        Assert.False(recorder.IsSystemAudioCaptured);
        Assert.False(recorder.IsAppAudioCaptured);
    }

    [Fact]
    public void Stop_SinHaberArrancado_NoTira()
    {
        using var recorder = new MeetingRecorder();
        recorder.Stop();
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void Dispose_SinHaberArrancado_NoTira()
    {
        var recorder = new MeetingRecorder();
        recorder.Dispose();
    }
}
