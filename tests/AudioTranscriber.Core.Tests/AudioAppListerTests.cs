using System.Linq;
using AudioTranscriber.Core.Audio;
using Xunit;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Solo la lógica pura de filtrado/dedup/orden (<see cref="AudioAppLister.SelectDistinctActiveApps"/>):
/// listar sesiones de audio REALES requiere un dispositivo de salida, eso no se testea acá (mismo
/// criterio que MeetingRecorder con el audio del sistema).
/// </summary>
public class AudioAppListerTests
{
    private const int OwnPid = 999;

    [Fact]
    public void SesionInactiva_SeExcluye()
    {
        var candidates = new[]
        {
            new AudioSessionCandidate(100, "Spotify", IsSystemSession: false, IsActive: false),
        };

        var result = AudioAppLister.SelectDistinctActiveApps(candidates, OwnPid);

        Assert.Empty(result);
    }

    [Fact]
    public void SesionDelSistema_SeExcluye()
    {
        var candidates = new[]
        {
            new AudioSessionCandidate(100, "Sonidos del sistema", IsSystemSession: true, IsActive: true),
        };

        var result = AudioAppLister.SelectDistinctActiveApps(candidates, OwnPid);

        Assert.Empty(result);
    }

    [Fact]
    public void NuestroPropioProceso_SeExcluye()
    {
        var candidates = new[]
        {
            new AudioSessionCandidate(OwnPid, "AudioTranscriber", IsSystemSession: false, IsActive: true),
        };

        var result = AudioAppLister.SelectDistinctActiveApps(candidates, OwnPid);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SinNombreResuelto_SeExcluye(string emptyName)
    {
        var candidates = new[]
        {
            new AudioSessionCandidate(100, emptyName, IsSystemSession: false, IsActive: true),
        };

        var result = AudioAppLister.SelectDistinctActiveApps(candidates, OwnPid);

        Assert.Empty(result);
    }

    [Fact]
    public void PidInvalido_SeExcluye()
    {
        var candidates = new[]
        {
            new AudioSessionCandidate(0, "Algo", IsSystemSession: false, IsActive: true),
            new AudioSessionCandidate(-1, "Algo", IsSystemSession: false, IsActive: true),
        };

        var result = AudioAppLister.SelectDistinctActiveApps(candidates, OwnPid);

        Assert.Empty(result);
    }

    /// Un mismo Chrome puede tener varias sesiones de audio (una por pestaña) con el MISMO PID
    /// (todas las pestañas comparten el proceso de red/GPU, pero el renderer puede repetirse):
    /// deduplica por PID, se queda con la primera.
    [Fact]
    public void MismoPidEnVariasSesiones_Deduplica()
    {
        var candidates = new[]
        {
            new AudioSessionCandidate(100, "Chrome - Pestaña 1", IsSystemSession: false, IsActive: true),
            new AudioSessionCandidate(100, "Chrome - Pestaña 2", IsSystemSession: false, IsActive: true),
        };

        var result = AudioAppLister.SelectDistinctActiveApps(candidates, OwnPid);

        var app = Assert.Single(result);
        Assert.Equal(100, app.ProcessId);
        Assert.Equal("Chrome - Pestaña 1", app.DisplayName);
    }

    [Fact]
    public void VariasApps_OrdenaAlfabeticamente()
    {
        var candidates = new[]
        {
            new AudioSessionCandidate(100, "Spotify", IsSystemSession: false, IsActive: true),
            new AudioSessionCandidate(200, "Chrome", IsSystemSession: false, IsActive: true),
            new AudioSessionCandidate(300, "Discord", IsSystemSession: false, IsActive: true),
        };

        var result = AudioAppLister.SelectDistinctActiveApps(candidates, OwnPid);

        Assert.Equal(new[] { "Chrome", "Discord", "Spotify" }, result.Select(a => a.DisplayName));
    }

    [Fact]
    public void SinSesiones_DevuelveListaVacia()
    {
        var result = AudioAppLister.SelectDistinctActiveApps(Enumerable.Empty<AudioSessionCandidate>(), OwnPid);

        Assert.Empty(result);
    }

    /// <summary>
    /// Caso real que motivó enumerar TODOS los dispositivos de salida y no solo el default de
    /// multimedia: Discord (y Zoom/Teams/Meet) usan el dispositivo de COMUNICACIONES de Windows,
    /// que suele ser otro. Ahora que las sesiones vienen de varios dispositivos, una misma app puede
    /// aparecer dos veces (una por dispositivo) y tiene que colapsar a una sola entrada del combo.
    /// </summary>
    [Fact]
    public void MismoPidEnDosDispositivosDistintos_ApareceUnaSolaVez()
    {
        var candidates = new[]
        {
            // Misma app, mismo PID, dos sesiones: una en los parlantes, otra en el headset.
            new AudioSessionCandidate(300, "Discord", IsSystemSession: false, IsActive: true),
            new AudioSessionCandidate(300, "Discord", IsSystemSession: false, IsActive: true),
            new AudioSessionCandidate(400, "obs64", IsSystemSession: false, IsActive: true),
        };

        var result = AudioAppLister.SelectDistinctActiveApps(candidates, OwnPid);

        Assert.Equal(new[] { "Discord", "obs64" }, result.Select(a => a.DisplayName));
    }

    /// <summary>
    /// Y la otra mitad del mismo caso: la sesión de una app puede estar INACTIVA en un dispositivo y
    /// activa en otro (Discord con la salida en el headset deja una sesión dormida en los parlantes).
    /// La app tiene que aparecer igual — si el dedup por PID se quedara con la sesión inactiva, el
    /// filtro de activas la borraría y Discord volvería a desaparecer del combo, que es justo el bug
    /// que se está arreglando.
    /// </summary>
    [Fact]
    public void AppActivaEnUnDispositivoEInactivaEnOtro_SigueApareciendo()
    {
        var candidates = new[]
        {
            new AudioSessionCandidate(300, "Discord", IsSystemSession: false, IsActive: false),
            new AudioSessionCandidate(300, "Discord", IsSystemSession: false, IsActive: true),
        };

        var result = AudioAppLister.SelectDistinctActiveApps(candidates, OwnPid);

        var app = Assert.Single(result);
        Assert.Equal(300, app.ProcessId);
        Assert.Equal("Discord", app.DisplayName);
    }
}
