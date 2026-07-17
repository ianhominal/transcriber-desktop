using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class PkceHelperTests
{
    [Fact]
    public void GenerateCodeVerifier_LongitudDentroDelRangoRfc7636()
    {
        var verifier = PkceHelper.GenerateCodeVerifier();

        Assert.InRange(verifier.Length, 43, 128);
    }

    [Fact]
    public void GenerateCodeVerifier_LongitudPersonalizada_SeRespeta()
    {
        var verifier = PkceHelper.GenerateCodeVerifier(43);

        Assert.Equal(43, verifier.Length);
    }

    [Fact]
    public void GenerateCodeVerifier_LongitudFueraDeRango_Lanza()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PkceHelper.GenerateCodeVerifier(42));
        Assert.Throws<ArgumentOutOfRangeException>(() => PkceHelper.GenerateCodeVerifier(129));
    }

    [Fact]
    public void GenerateCodeVerifier_SoloUsaCharsetUnreservedUrlSafe()
    {
        var verifier = PkceHelper.GenerateCodeVerifier();

        Assert.Matches("^[A-Za-z0-9\\-._~]+$", verifier);
    }

    [Fact]
    public void GenerateCodeVerifier_GeneraValoresDistintosEnCadaLlamada()
    {
        var a = PkceHelper.GenerateCodeVerifier();
        var b = PkceHelper.GenerateCodeVerifier();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CreateCodeChallenge_CoincideConVectorDePruebaDelRfc7636()
    {
        // Vector de prueba oficial del Apéndice B de RFC 7636.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        var challenge = PkceHelper.CreateCodeChallenge(verifier);

        Assert.Equal(expectedChallenge, challenge);
    }

    [Fact]
    public void CreateCodeChallenge_NoContienePaddingNiSimbolosBase64Estandar()
    {
        var verifier = PkceHelper.GenerateCodeVerifier();

        var challenge = PkceHelper.CreateCodeChallenge(verifier);

        Assert.DoesNotContain('=', challenge);
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
    }
}
