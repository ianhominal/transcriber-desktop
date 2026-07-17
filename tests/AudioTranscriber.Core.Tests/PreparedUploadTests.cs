using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

public class PreparedUploadTests
{
    [Fact]
    public void Parse_reads_path_signedUrl_and_apiKey()
    {
        var result = PreparedUpload.Parse(
            """{"path":"user-1/abc.ogg","signedUrl":"https://x/storage/upload?token=t","apiKey":"anon-key"}""");

        Assert.Equal("user-1/abc.ogg", result.Path);
        Assert.Equal("https://x/storage/upload?token=t", result.SignedUrl);
        Assert.Equal("anon-key", result.ApiKey);
    }

    [Fact]
    public void Parse_ignores_extra_fields()
    {
        var result = PreparedUpload.Parse("""{"path":"p","signedUrl":"u","apiKey":"k","extra":123}""");
        Assert.Equal("p", result.Path);
        Assert.Equal("u", result.SignedUrl);
        Assert.Equal("k", result.ApiKey);
    }

    [Theory]
    [InlineData("""{"signedUrl":"u","apiKey":"k"}""")]  // falta path
    [InlineData("""{"path":"p","apiKey":"k"}""")]       // falta signedUrl
    [InlineData("""{"path":"p","signedUrl":"u"}""")]    // falta apiKey
    [InlineData("""{"path":"","signedUrl":"u","apiKey":"k"}""")]  // path vacío
    [InlineData("""{"path":"p","signedUrl":"","apiKey":"k"}""")]  // signedUrl vacío
    [InlineData("""{"path":"p","signedUrl":"u","apiKey":""}""")]  // apiKey vacío
    [InlineData("{}")]                                            // ninguno
    public void Parse_throws_SyncApiException_when_a_field_is_missing_or_empty(string json)
    {
        Assert.Throws<SyncApiException>(() => PreparedUpload.Parse(json));
    }

    [Theory]
    [InlineData("no soy json")]
    [InlineData("")]
    [InlineData("[1,2,3]")] // JSON válido pero no un objeto
    public void Parse_throws_SyncApiException_not_JsonException_for_invalid_bodies(string json)
    {
        // Importante: NO debe escapar una JsonException -- si escapara, abortaría el batch entero
        // del ciclo de sync (el catch de RunAsync solo atrapa SyncApiException/HttpRequestException/IOException).
        Assert.Throws<SyncApiException>(() => PreparedUpload.Parse(json));
    }
}
