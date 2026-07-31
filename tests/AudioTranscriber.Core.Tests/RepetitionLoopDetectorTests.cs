using AudioTranscriber.Core.Transcription;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Caso real que motivó esto: una grabación de pantalla casi sin habla salió transcrita como
/// "Mae'r cydnabod yn dda iawn." (galés) repetido cientos de veces, y la tester tuvo que esperar a
/// que terminara igual. Whisper alimenta el texto del segmento anterior como prompt del siguiente,
/// así que una alucinación se auto-refuerza y se muerde la cola.
/// </summary>
public class RepetitionLoopDetectorTests
{
    [Fact]
    public void NoDisparaConHablaNormal()
    {
        var detector = new RepetitionLoopDetector();

        Assert.False(detector.Observe("Hola, buenas tardes."));
        Assert.False(detector.Observe("Hoy vamos a hablar del proyecto."));
        Assert.False(detector.Observe("Primero, el presupuesto."));
    }

    [Fact]
    public void NoDisparaConRepeticionesLegitimasCortas()
    {
        var detector = new RepetitionLoopDetector();

        // En habla real alguien puede repetir una muletilla dos o tres veces seguidas.
        Assert.False(detector.Observe("Sí."));
        Assert.False(detector.Observe("Sí."));
        Assert.False(detector.Observe("Sí."));
    }

    [Fact]
    public void DisparaCuandoElMismoSegmentoSeRepiteMuchasVeces()
    {
        var detector = new RepetitionLoopDetector();
        const string alucinacion = "Mae'r cydnabod yn dda iawn.";

        var disparo = false;
        for (var i = 0; i < RepetitionLoopDetector.UmbralPorDefecto; i++)
            disparo = detector.Observe(alucinacion);

        Assert.True(disparo);
    }

    [Fact]
    public void IgnoraDiferenciasDeEspaciosMayusculasYPuntuacion()
    {
        var detector = new RepetitionLoopDetector();

        var disparo = false;
        for (var i = 0; i < RepetitionLoopDetector.UmbralPorDefecto; i++)
            disparo = detector.Observe(i % 2 == 0 ? "  mae'r CYDNABOD yn dda iawn  " : "Mae'r cydnabod yn dda iawn.");

        Assert.True(disparo);
    }

    [Fact]
    public void UnSegmentoDistintoCortaLaRacha()
    {
        var detector = new RepetitionLoopDetector(umbral: 4);

        Assert.False(detector.Observe("Repetido."));
        Assert.False(detector.Observe("Repetido."));
        Assert.False(detector.Observe("Repetido."));
        Assert.False(detector.Observe("Algo distinto."));
        // La racha volvió a empezar: tres repeticiones más no alcanzan el umbral de 4.
        Assert.False(detector.Observe("Repetido."));
        Assert.False(detector.Observe("Repetido."));
        Assert.False(detector.Observe("Repetido."));
    }

    [Fact]
    public void IgnoraSegmentosVaciosOSoloEspacios()
    {
        var detector = new RepetitionLoopDetector(umbral: 3);

        // Los silencios suelen emitir segmentos vacíos: no son una alucinación en loop.
        Assert.False(detector.Observe(""));
        Assert.False(detector.Observe("   "));
        Assert.False(detector.Observe(""));
        Assert.False(detector.Observe("   "));
    }

    [Fact]
    public void SigueEnTrueUnaVezQueDisparo()
    {
        var detector = new RepetitionLoopDetector(umbral: 2);

        detector.Observe("Loop.");
        Assert.True(detector.Observe("Loop."));
        // El caller corta el streaming, pero si llega un segmento más no puede "des-detectarse".
        Assert.True(detector.Detectado);
    }

    [Fact]
    public void UmbralInvalidoNoEsAceptado()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepetitionLoopDetector(umbral: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepetitionLoopDetector(umbral: 0));
    }
}
