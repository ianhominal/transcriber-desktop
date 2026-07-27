using AudioTranscriber.Core.Meetings;
using Xunit;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Máquina de estados pura de detección de reuniones (brief "detectar reunión en curso y ofrecer
/// grabarla"): a partir de qué apps están usando el micrófono ahora mismo (snapshot del registro
/// ConsentStore de Windows, leído por MicrophoneUsageReader en la capa App -- acá no hay registro
/// ni I/O, solo listas de strings a mano). Cubre las reglas del brief: (a) app de reunión con el
/// mic -> EnReunion=true, (b) solo el propio proceso -> no cuenta, (c)/(d) una sola transición por
/// cambio de estado, (e) app no-reunión -> no dispara, + debounce (N snapshots seguidos).
/// </summary>
public class MeetingDetectorTests
{
    private const string Self = @"C:\Program Files\AudioTranscriber\AudioTranscriber.exe";

    // ---- MatchesMeetingApp: patrón conocido, case-insensitive, substring sobre rutas completas ----

    [Theory]
    [InlineData("chrome")]
    [InlineData("CHROME.EXE")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("brave")]
    [InlineData("opera")]
    [InlineData("Zoom")]
    [InlineData("ms-teams")]
    [InlineData("Teams")]
    [InlineData("Discord")]
    public void MatchesMeetingApp_AppDeReunion_DevuelveTrue(string identifier) =>
        Assert.True(MeetingDetector.MatchesMeetingApp(identifier));

    [Theory]
    [InlineData("notepad")]
    [InlineData("spotify")]
    [InlineData("obs64")]
    [InlineData("")]
    public void MatchesMeetingApp_AppNoDeReunion_DevuelveFalse(string identifier) =>
        Assert.False(MeetingDetector.MatchesMeetingApp(identifier));

    // ---- Regla (a): app de reunión usando el mic -> EnReunion=true (sin debounce, N=1) ----

    [Fact]
    public void Update_ConAppDeReunionUsandoElMic_QuedaEnReunion()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 1);

        var transition = detector.Update(new[] { "chrome" }, Self);

        Assert.Equal(MeetingTransition.Started, transition);
        Assert.True(detector.InMeeting);
    }

    // ---- Regla (b): solo el propio proceso usando el mic (grabando) -> no cuenta ----

    [Fact]
    public void Update_SoloElPropioProcesoUsandoElMic_NoCuentaComoReunion()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 1);

        var transition = detector.Update(new[] { Self }, Self);

        Assert.Equal(MeetingTransition.None, transition);
        Assert.False(detector.InMeeting);
    }

    [Fact]
    public void Update_AppDeReunionMasElPropioProceso_ExcluyeSoloElPropio()
    {
        // Una app de reunión de verdad (chrome) + esta misma app grabando -- el propio proceso se
        // excluye del match, pero chrome sigue contando.
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 1);

        var transition = detector.Update(new[] { Self, "chrome" }, Self);

        Assert.Equal(MeetingTransition.Started, transition);
        Assert.True(detector.InMeeting);
    }

    // ---- Regla (e): app no-reunión usando el mic -> no dispara ----

    [Fact]
    public void Update_AppNoDeReunionUsandoElMic_NoDispara()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 1);

        var transition = detector.Update(new[] { "obs64" }, Self);

        Assert.Equal(MeetingTransition.None, transition);
        Assert.False(detector.InMeeting);
    }

    [Fact]
    public void Update_SinNadieUsandoElMic_NoDispara()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 1);

        var transition = detector.Update(Array.Empty<string>(), Self);

        Assert.Equal(MeetingTransition.None, transition);
        Assert.False(detector.InMeeting);
    }

    // ---- Reglas (c)/(d): UNA sola transición por cambio de estado ----

    [Fact]
    public void Update_TransicionNotInToIn_DisparaStartedUnaSolaVez()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 1);

        var first = detector.Update(new[] { "zoom" }, Self);
        var second = detector.Update(new[] { "zoom" }, Self); // sigue en la misma reunión

        Assert.Equal(MeetingTransition.Started, first);
        Assert.Equal(MeetingTransition.None, second);
    }

    [Fact]
    public void Update_TransicionInToOut_DisparaEndedUnaSolaVez()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 1);
        detector.Update(new[] { "zoom" }, Self); // arranca

        var ended = detector.Update(Array.Empty<string>(), Self);
        var stillOut = detector.Update(Array.Empty<string>(), Self);

        Assert.Equal(MeetingTransition.Ended, ended);
        Assert.False(detector.InMeeting);
        Assert.Equal(MeetingTransition.None, stillOut);
    }

    [Fact]
    public void Update_SePuedeDetectarUnaSegundaReunionDespuesDeTerminarLaPrimera()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 1);
        detector.Update(new[] { "zoom" }, Self);   // reunión 1 arranca
        detector.Update(Array.Empty<string>(), Self); // reunión 1 termina

        var secondMeeting = detector.Update(new[] { "discord" }, Self);

        Assert.Equal(MeetingTransition.Started, secondMeeting);
        Assert.True(detector.InMeeting);
    }

    // ---- Debounce: hacen falta N snapshots seguidos en el mismo sentido ----

    [Fact]
    public void Update_ConDebounce_UnSoloSnapshotNoAlcanzaParaArrancar()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 3);

        var transition = detector.Update(new[] { "chrome" }, Self);

        Assert.Equal(MeetingTransition.None, transition);
        Assert.False(detector.InMeeting);
    }

    [Fact]
    public void Update_ConDebounce_NSnapshotsSeguidosDisparaStarted()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 3);

        detector.Update(new[] { "chrome" }, Self);
        detector.Update(new[] { "chrome" }, Self);
        var third = detector.Update(new[] { "chrome" }, Self);

        Assert.Equal(MeetingTransition.Started, third);
        Assert.True(detector.InMeeting);
    }

    [Fact]
    public void Update_ConDebounce_UnBlipCortoReiniciaElContador()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 3);

        detector.Update(new[] { "chrome" }, Self);
        detector.Update(new[] { "chrome" }, Self);
        detector.Update(Array.Empty<string>(), Self); // blip: se corta un tick
        detector.Update(new[] { "chrome" }, Self);
        detector.Update(new[] { "chrome" }, Self);
        var fifthConsecutive = detector.Update(new[] { "chrome" }, Self);

        // Recién acá van 3 SEGUIDOS de nuevo -- el blip de antes reinició el contador.
        Assert.Equal(MeetingTransition.Started, fifthConsecutive);
        Assert.True(detector.InMeeting);
    }

    [Fact]
    public void Update_ConDebounce_TerminarTambienRequiereNSnapshotsSeguidos()
    {
        var detector = new MeetingDetector(requiredConsecutiveSnapshots: 2);
        detector.Update(new[] { "teams" }, Self);
        detector.Update(new[] { "teams" }, Self); // InMeeting = true

        var firstGap = detector.Update(Array.Empty<string>(), Self);
        Assert.Equal(MeetingTransition.None, firstGap);
        Assert.True(detector.InMeeting); // un solo snapshot sin mic no alcanza para "terminó"

        var secondGap = detector.Update(Array.Empty<string>(), Self);
        Assert.Equal(MeetingTransition.Ended, secondGap);
        Assert.False(detector.InMeeting);
    }

    // ---- Guarda del constructor ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ConDebounceMenorAUno_Tira(int requiredConsecutiveSnapshots) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeetingDetector(requiredConsecutiveSnapshots));
}
