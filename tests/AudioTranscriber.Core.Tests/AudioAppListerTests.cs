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
}
