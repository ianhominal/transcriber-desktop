using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class UserProfileFormatterTests
{
    [Fact]
    public void NombreConDosPalabras_TomaPrimeraLetraDeCadaUna()
    {
        var initials = UserProfileFormatter.GetInitials("Ian Hominal");

        Assert.Equal("IH", initials);
    }

    [Fact]
    public void NombreConUnaSolaPalabra_TomaSoloEsaLetra()
    {
        var initials = UserProfileFormatter.GetInitials("Ian");

        Assert.Equal("I", initials);
    }

    [Fact]
    public void NombreConMasDeDosPalabras_IgnoraElResto()
    {
        var initials = UserProfileFormatter.GetInitials("Ian Emanuel Hominal");

        Assert.Equal("IE", initials);
    }

    [Fact]
    public void NombreEnMinuscula_SeConvierteAMayuscula()
    {
        var initials = UserProfileFormatter.GetInitials("ian hominal");

        Assert.Equal("IH", initials);
    }

    [Fact]
    public void NombreConEspaciosDeSobra_LosIgnora()
    {
        var initials = UserProfileFormatter.GetInitials("  Ian   Hominal  ");

        Assert.Equal("IH", initials);
    }

    [Fact]
    public void NombreVacio_DevuelveSignoDePregunta()
    {
        Assert.Equal("?", UserProfileFormatter.GetInitials(""));
    }

    [Fact]
    public void NombreNull_DevuelveSignoDePregunta()
    {
        Assert.Equal("?", UserProfileFormatter.GetInitials(null));
    }

    [Fact]
    public void NombreSoloEspacios_DevuelveSignoDePregunta()
    {
        Assert.Equal("?", UserProfileFormatter.GetInitials("   "));
    }
}
