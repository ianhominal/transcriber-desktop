using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncErrorClassifierTests
{
    [Fact]
    public void Classify_SyncAuthException400_EsNeedsLogin()
    {
        var ex = new SyncAuthException("Autenticación falló (400): invalid_grant", 400);

        Assert.Equal(SyncErrorCategory.NeedsLogin, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_SyncApiException401_EsNeedsLogin()
    {
        var ex = new SyncApiException("pull falló (401): No autorizado.", 401);

        Assert.Equal(SyncErrorCategory.NeedsLogin, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_SyncApiException403_EsNeedsLogin()
    {
        var ex = new SyncApiException("push falló (403): Forbidden", 403);

        Assert.Equal(SyncErrorCategory.NeedsLogin, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_MensajeConRefreshTokenNotFound_EsNeedsLogin()
    {
        // El backend puede rechazar con otro status pero el motivo real viene en el body.
        var ex = new SyncApiException("push falló (400): refresh_token_not_found");

        Assert.Equal(SyncErrorCategory.NeedsLogin, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_SyncApiException400SinRefreshTokenNotFound_EsUnknown()
    {
        // 400 solo no alcanza para SyncApiException (a diferencia de SyncAuthException, donde el
        // refresh rechazado con 400 SÍ es sesión inválida) -- necesita 401/403 o el motivo explícito.
        var ex = new SyncApiException("push falló (400): Bad Request", 400);

        Assert.Equal(SyncErrorCategory.Unknown, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_SyncApiException500_EsServerError()
    {
        var ex = new SyncApiException("push falló (500): Internal Server Error", 500);

        Assert.Equal(SyncErrorCategory.ServerError, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_SyncApiException503_EsServerError()
    {
        var ex = new SyncApiException("pull falló (503): Service Unavailable", 503);

        Assert.Equal(SyncErrorCategory.ServerError, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_SyncAuthException500_EsServerError()
    {
        var ex = new SyncAuthException("Autenticación falló (500): Internal Server Error", 500);

        Assert.Equal(SyncErrorCategory.ServerError, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_HttpRequestException_EsNetworkError()
    {
        var ex = new HttpRequestException("No se pudo resolver el host.");

        Assert.Equal(SyncErrorCategory.NetworkError, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_TaskCanceledException_EsNetworkError()
    {
        // HttpClient tira TaskCanceledException cuando se cumple el timeout configurado.
        var ex = new TaskCanceledException("La operación fue cancelada.");

        Assert.Equal(SyncErrorCategory.NetworkError, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_SocketException_EsNetworkError()
    {
        var ex = new System.Net.Sockets.SocketException();

        Assert.Equal(SyncErrorCategory.NetworkError, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_ExcepcionGenerica_EsUnknown()
    {
        var ex = new InvalidOperationException("bug inesperado del cliente");

        Assert.Equal(SyncErrorCategory.Unknown, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_SyncApiExceptionSinStatusCode_EsUnknown()
    {
        // Constructor de 1 solo argumento: StatusCode queda null (no es un fallo HTTP clasificable
        // como sesión inválida ni error de servidor -- p.ej. una falla de deserialización).
        var ex = new SyncApiException("deserialización falló");

        Assert.Equal(SyncErrorCategory.Unknown, SyncErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SyncErrorClassifier.Classify(null!));
    }
}
