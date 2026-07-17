using System.Text.Json;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class DesktopSessionRequestBuilderTests
{
    [Fact]
    public void BuildJsonBody_ConTokensValidos_SerializaAmbosCampos()
    {
        var json = DesktopSessionRequestBuilder.BuildJsonBody("access-abc", "refresh-xyz");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("access-abc", doc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("refresh-xyz", doc.RootElement.GetProperty("refresh_token").GetString());
    }

    [Fact]
    public void BuildJsonBody_UsaLosNombresDePropiedadDelContratoSnakeCase()
    {
        var json = DesktopSessionRequestBuilder.BuildJsonBody("t1", "t2");

        Assert.Contains("\"access_token\"", json);
        Assert.Contains("\"refresh_token\"", json);
    }

    [Fact]
    public void BuildJsonBody_ConCaracteresEspeciales_ProduceJsonValido()
    {
        // Un JWT real nunca trae comillas/backslashes, pero el builder no debe asumirlo: si
        // algún día el token viene con caracteres que rompen JSON sin escapar, esto lo detecta.
        var json = DesktopSessionRequestBuilder.BuildJsonBody("a\"b\\c", "x\ny");

        using var doc = JsonDocument.Parse(json); // no debe tirar FormatException
        Assert.Equal("a\"b\\c", doc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("x\ny", doc.RootElement.GetProperty("refresh_token").GetString());
    }
}
