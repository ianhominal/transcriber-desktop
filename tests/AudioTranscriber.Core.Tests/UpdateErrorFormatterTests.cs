using System;
using System.Net;
using System.Net.Http;
using AudioTranscriber.Core.Updates;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class UpdateErrorFormatterTests
{
    /// La forma EXACTA en que llegó el caso real: mensaje con el 429 adentro y sin StatusCode.
    private static HttpRequestException RealRateLimitFailure() =>
        new("Response status code does not indicate success: 429 (too many requests).");

    [Fact]
    public void RateLimit_NoCulpaALaConexionDelUsuario()
    {
        var message = UpdateErrorFormatter.Describe(RealRateLimitFailure());

        Assert.DoesNotContain("conexión", message);
        Assert.Contains("Esperá", message);
    }

    [Fact]
    public void RateLimit_ConStatusCode_TambienSeDetecta()
    {
        var ex = new HttpRequestException("nope", null, HttpStatusCode.TooManyRequests);

        Assert.Contains("Demasiadas consultas", UpdateErrorFormatter.Describe(ex));
    }

    /// Sin StatusCode y sin rastro del 429 = nunca hubo respuesta: ACÁ sí es la conexión.
    [Fact]
    public void SinRespuesta_SiCulpaALaConexion()
    {
        var ex = new HttpRequestException("No such host is known.");

        Assert.Contains("Revisá tu conexión", UpdateErrorFormatter.Describe(ex));
    }

    [Fact]
    public void ErrorDeServidor_DiceQueEsDeGitHub_NoDelUsuario()
    {
        var ex = new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable);
        var message = UpdateErrorFormatter.Describe(ex);

        Assert.Contains("GitHub", message);
        Assert.DoesNotContain("conexión", message);
    }

    [Fact]
    public void NotFound_TieneSuPropioMensaje()
    {
        var ex = new HttpRequestException("nope", null, HttpStatusCode.NotFound);

        Assert.Contains("No se encontró", UpdateErrorFormatter.Describe(ex));
    }

    [Fact]
    public void Timeout_DiceQueTardoDemasiado()
    {
        Assert.Contains("tardó demasiado", UpdateErrorFormatter.Describe(new TaskCanceledException()));
        Assert.Contains("tardó demasiado", UpdateErrorFormatter.Describe(new TimeoutException()));
    }

    [Fact]
    public void ExcepcionDesconocida_CaeAUnMensajeGenerico_SinInventarCausas()
    {
        var message = UpdateErrorFormatter.Describe(new InvalidOperationException("algo raro"));

        Assert.Equal("No se pudo verificar si hay una versión nueva. Probá más tarde.", message);
        Assert.DoesNotContain("conexión", message);
    }

    [Fact]
    public void NuncaFiltraElTextoCrudoDeLaExcepcion()
    {
        foreach (var ex in new Exception[]
                 {
                     RealRateLimitFailure(),
                     new HttpRequestException("Response status code does not indicate success: 500."),
                     new InvalidOperationException("System.Net.Http.HttpRequestException: boom"),
                 })
        {
            var message = UpdateErrorFormatter.Describe(ex);
            Assert.DoesNotContain("status code", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Exception", message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
