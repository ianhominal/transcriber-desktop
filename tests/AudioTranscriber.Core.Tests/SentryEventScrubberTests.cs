using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.Core.Tests;

public class SentryEventScrubberTests
{
    // ---- IsSensitiveKey ---------------------------------------------------

    [Theory]
    [InlineData("accessToken")]
    [InlineData("sync.refreshToken")]
    [InlineData("GroqApiKeyProtected")]
    [InlineData("password")]
    [InlineData("Authorization")]
    [InlineData("client_secret")]
    [InlineData("session_cookie")]
    public void IsSensitiveKey_ClavesConocidas_DevuelveTrue(string key)
    {
        Assert.True(SentryEventScrubber.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("SyncFolder")]
    [InlineData("UserName")]
    [InlineData("UserEmail")]
    [InlineData("Engine")]
    public void IsSensitiveKey_ClavesNoSensibles_DevuelveFalse(string key)
    {
        Assert.False(SentryEventScrubber.IsSensitiveKey(key));
    }

    // ---- ScrubText ----------------------------------------------------------

    [Fact]
    public void ScrubText_Nulo_DevuelveNulo()
    {
        Assert.Null(SentryEventScrubber.ScrubText(null));
    }

    [Fact]
    public void ScrubText_Vacio_DevuelveVacio()
    {
        Assert.Equal(string.Empty, SentryEventScrubber.ScrubText(string.Empty));
    }

    [Fact]
    public void ScrubText_TextoSinDatosSensibles_QuedaIgual()
    {
        var texto = "pull falló (401): backend rechazó la request.";
        Assert.Equal(texto, SentryEventScrubber.ScrubText(texto));
    }

    [Fact]
    public void ScrubText_TokenTipoJwtIncrustado_SeReemplaza()
    {
        var jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var texto = $"token guardado: {jwt} (vencido)";

        var result = SentryEventScrubber.ScrubText(texto);

        Assert.DoesNotContain(jwt, result);
        Assert.Contains("[scrubbed]", result);
        Assert.Contains("(vencido)", result); // el resto del mensaje sigue siendo legible
    }

    [Fact]
    public void ScrubText_HeaderBearer_SeReemplaza()
    {
        var texto = "Authorization: Bearer abc123.def456-token_real";

        var result = SentryEventScrubber.ScrubText(texto);

        Assert.DoesNotContain("abc123.def456-token_real", result);
        Assert.Contains("Bearer [scrubbed]", result);
    }

    [Fact]
    public void ScrubText_RutaDeUsuarioWindows_OcultaElNombreDeUsuario()
    {
        var texto = @"No se pudo escribir en C:\Users\ianhominal\AppData\Local\AudioTranscriber\sync.log";

        var result = SentryEventScrubber.ScrubText(texto);

        Assert.DoesNotContain("ianhominal", result);
        Assert.Contains(@"C:\Users\[scrubbed]\AppData\Local\AudioTranscriber\sync.log", result);
    }

    // ---- ScrubValueForKey ----------------------------------------------------

    [Fact]
    public void ScrubValueForKey_ClaveSensible_SiempreDevuelveElPlaceholder_SinImportarElValor()
    {
        Assert.Equal("[scrubbed]", SentryEventScrubber.ScrubValueForKey("sync.accessToken", "cualquier-valor-inocuo"));
    }

    [Fact]
    public void ScrubValueForKey_ClaveNoSensible_AplicaScrubTextIgual()
    {
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ1c2VyIn0.abcDEF123-signature_here";
        var result = SentryEventScrubber.ScrubValueForKey("lastError", $"fallo: {jwt}");

        Assert.DoesNotContain(jwt, result);
    }

    [Fact]
    public void ScrubValueForKey_ClaveNoSensibleValorLimpio_QuedaIgual()
    {
        Assert.Equal("Sincronizado ✓", SentryEventScrubber.ScrubValueForKey("statusMessage", "Sincronizado ✓"));
    }

    // ---- ScrubDictionary ------------------------------------------------------

    [Fact]
    public void ScrubDictionary_MezclaDeClavesSensiblesYNormales_SoloScrubbeaLasSensibles()
    {
        var original = new Dictionary<string, string?>
        {
            ["sync.accessToken"] = "token-secreto",
            ["statusMessage"] = "Sincronizado ✓",
            ["refreshToken"] = "otro-secreto",
        };

        var result = SentryEventScrubber.ScrubDictionary(original);

        Assert.Equal("[scrubbed]", result["sync.accessToken"]);
        Assert.Equal("[scrubbed]", result["refreshToken"]);
        Assert.Equal("Sincronizado ✓", result["statusMessage"]);
    }

    [Fact]
    public void ScrubDictionary_NoMutaElDiccionarioOriginal()
    {
        var original = new Dictionary<string, string?> { ["password"] = "1234" };

        SentryEventScrubber.ScrubDictionary(original);

        Assert.Equal("1234", original["password"]); // la copia no debe afectar al original
    }
}
