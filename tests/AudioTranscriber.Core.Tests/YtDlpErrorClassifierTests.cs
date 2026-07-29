using AudioTranscriber.Core.WebImport;

namespace AudioTranscriber.Core.Tests;

public class YtDlpErrorClassifierTests
{
    [Theory]
    [InlineData("ERROR: [youtube] abc123: Private video. Sign in if you've been granted access to this video")]
    [InlineData("ERROR: [youtube] abc123: Sign in to confirm your age")]
    [InlineData("ERROR: This video is only available to Music Premium members")]
    public void Detecta_contenido_privado_o_que_requiere_login(string stderr)
    {
        var (status, message) = YtDlpErrorClassifier.Classify(1, stderr);

        Assert.Equal(WebImportStatus.RequiresLogin, status);
        Assert.Equal("El video es privado o requiere iniciar sesión para verlo.", message);
    }

    [Fact]
    public void Detecta_url_no_soportada()
    {
        var (status, _) = YtDlpErrorClassifier.Classify(1, "ERROR: Unsupported URL: ftp://algo");

        Assert.Equal(WebImportStatus.InvalidUrl, status);
    }

    [Theory]
    [InlineData("ERROR: [generic] No video formats found!")]
    [InlineData("ERROR: Unable to extract video data")]
    public void Detecta_pagina_sin_audio(string stderr)
    {
        var (status, message) = YtDlpErrorClassifier.Classify(1, stderr);

        Assert.Equal(WebImportStatus.NoAudioFound, status);
        Assert.Equal("No se encontró audio en esa página.", message);
    }

    [Theory]
    [InlineData("ERROR: [generic] Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>")]
    [InlineData("ERROR: Network is unreachable")]
    public void Detecta_falla_de_conexion(string stderr)
    {
        var (status, message) = YtDlpErrorClassifier.Classify(1, stderr);

        Assert.Equal(WebImportStatus.NoConnection, status);
        Assert.Equal("Sin conexión a internet. Revisá tu conexión e intentá de nuevo.", message);
    }

    [Fact]
    public void Error_no_reconocido_cae_a_YtDlpFailed_con_la_primera_linea_de_stderr()
    {
        var (status, message) = YtDlpErrorClassifier.Classify(2, "ERROR: algo raro pasó\nmás detalle acá");

        Assert.Equal(WebImportStatus.YtDlpFailed, status);
        Assert.Contains("ERROR: algo raro pasó", message);
        Assert.DoesNotContain("más detalle acá", message);
    }

    [Fact]
    public void Stderr_nulo_o_vacio_no_revienta()
    {
        var (status, message) = YtDlpErrorClassifier.Classify(1, null);

        Assert.Equal(WebImportStatus.YtDlpFailed, status);
        Assert.Contains("error desconocido", message);
    }

    // ---- Mensajes capturados de una corrida REAL de yt-dlp 2026.07.04 -------------------------
    // Los de abajo no son inventados: se ejecutó la herramienta de verdad contra URLs rotas para
    // ver qué escribe exactamente en stderr. Los fixtures del parser ya se habían validado igual.

    [Fact]
    public void Pagina_que_no_existe_no_se_reporta_como_falta_de_conexion()
    {
        // Texto REAL de yt-dlp ante una URL 404. Contiene "unable to download webpage", que estaba
        // en la lista de errores de red: el usuario terminaba revisando su router por un link roto.
        // Es el mismo error que ya se cometió en el repo web (ver gotcha del CLAUDE.md: "revisá tu
        // conexión" mandó a alguien a revisar el router durante horas por un 429 de GitHub).
        var stderr = "ERROR: [generic] nada: Unable to download webpage: HTTP Error 404: Not Found (caused by <HTTPError 404: Not Found>)";

        var (status, message) = YtDlpErrorClassifier.Classify(1, stderr);

        Assert.NotEqual(WebImportStatus.NoConnection, status);
        Assert.DoesNotContain("conexión", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Video_dado_de_baja_tiene_su_propio_mensaje()
    {
        // Texto REAL ante un id de YouTube inexistente. Antes caía al genérico
        // "Falló yt-dlp (código 1): ...", que no le dice nada a nadie.
        var (status, message) = YtDlpErrorClassifier.Classify(1, "ERROR: [youtube] aaaaaaaaaaa: Video unavailable");

        Assert.Equal(WebImportStatus.NoAudioFound, status);
        Assert.Contains("ya no está disponible", message);
    }

    [Fact]
    public void Un_fallo_de_red_de_verdad_sigue_reportandose_como_tal()
    {
        // La contraparte del test de arriba: al arreglar el 404 no hay que romper el caso legítimo.
        var (status, _) = YtDlpErrorClassifier.Classify(1, "ERROR: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>");

        Assert.Equal(WebImportStatus.NoConnection, status);
    }
}
