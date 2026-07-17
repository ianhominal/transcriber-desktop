using System.Net.Http;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class SyncFailureClassifierTests
{
    [Theory]
    [InlineData(413)] // Payload Too Large: el audio no entra
    [InlineData(415)] // Unsupported Media Type
    public void Es_permanente_para_413_y_415(int status) =>
        Assert.True(SyncFailureClassifier.IsPermanentUploadFailure(new SyncApiException("x", status)));

    [Theory]
    [InlineData(500)] // error del server, puede ser pasajero
    [InlineData(503)]
    [InlineData(429)] // rate limit
    [InlineData(401)] // auth: se resuelve refrescando el token, no descartando el ítem
    [InlineData(408)] // timeout
    public void Es_transitorio_para_el_resto_de_los_status(int status) =>
        Assert.False(SyncFailureClassifier.IsPermanentUploadFailure(new SyncApiException("x", status)));

    [Fact]
    public void Es_transitorio_para_fallas_sin_status_http()
    {
        Assert.False(SyncFailureClassifier.IsPermanentUploadFailure(new HttpRequestException("sin red")));
        Assert.False(SyncFailureClassifier.IsPermanentUploadFailure(new IOException("disco")));
        // SyncApiException sin código HTTP (red/timeout/deserialización): no se puede afirmar que sea permanente.
        Assert.False(SyncFailureClassifier.IsPermanentUploadFailure(new SyncApiException("sin status")));
    }
}
