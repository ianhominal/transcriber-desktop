using AudioTranscriber.Core.WebImport;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Parseo puro de la salida de <c>yt-dlp --dump-single-json --flat-playlist</c>. Sin red, sin
/// proceso: solo strings JSON de entrada (fixtures reales en <c>Fixtures/</c>) y aserciones sobre
/// el modelo resultante. Ver <see cref="YtDlpJsonParser"/>.
/// </summary>
public class YtDlpJsonParserTests
{
    private static string ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"No se encontró 'Fixtures/{name}' en {AppContext.BaseDirectory}. Revisá el csproj (Fixtures\\*.json).",
                path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void Parse_video_suelto_devuelve_un_solo_item_con_todos_los_campos()
    {
        var json = ReadFixture("ytdlp-single-video.json");

        var analysis = YtDlpJsonParser.Parse(json);

        Assert.False(analysis.IsPlaylist);
        Assert.Null(analysis.PlaylistTitle);
        var item = Assert.Single(analysis.Items);
        Assert.Equal(1, item.Index);
        Assert.Equal("dQw4w9WgXcQ", item.Id);
        Assert.Equal("Rick Astley - Never Gonna Give You Up", item.Title);
        Assert.Equal(TimeSpan.FromSeconds(212), item.Duration);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", item.Url);
    }

    [Fact]
    public void Parse_playlist_devuelve_todas_las_entradas_en_orden_con_indice_1_based()
    {
        var json = ReadFixture("ytdlp-playlist.json");

        var analysis = YtDlpJsonParser.Parse(json);

        Assert.True(analysis.IsPlaylist);
        Assert.Equal("Charlas sobre arquitectura", analysis.PlaylistTitle);
        Assert.Equal(3, analysis.Items.Count);

        Assert.Equal(1, analysis.Items[0].Index);
        Assert.Equal("abc123", analysis.Items[0].Id);
        Assert.Equal("Charla 1: Clean Architecture", analysis.Items[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(305), analysis.Items[0].Duration);

        Assert.Equal(2, analysis.Items[1].Index);
        Assert.Null(analysis.Items[1].Duration);

        Assert.Equal(3, analysis.Items[2].Index);
        Assert.Equal(TimeSpan.FromSeconds(1523.7), analysis.Items[2].Duration);
    }

    [Fact]
    public void Parse_sitio_generico_con_audio_embebido_lo_trata_como_item_suelto()
    {
        var json = ReadFixture("ytdlp-generic-embedded.json");

        var analysis = YtDlpJsonParser.Parse(json);

        Assert.False(analysis.IsPlaylist);
        var item = Assert.Single(analysis.Items);
        Assert.Equal("episodio-42", item.Id);
        Assert.Equal("Episodio 42: Charla sobre yt-dlp", item.Title);
        Assert.Equal(TimeSpan.FromSeconds(1834.5), item.Duration);
        Assert.Equal("https://example.com/blog/episodio-42", item.Url);
    }

    [Fact]
    public void Parse_json_con_campos_faltantes_usa_fallbacks_sin_reventar()
    {
        var json = ReadFixture("ytdlp-missing-fields.json");

        var analysis = YtDlpJsonParser.Parse(json);

        var item = Assert.Single(analysis.Items);
        Assert.Equal("raw_page_042", item.Id);
        // Sin "title" en el JSON: cae al id (mejor que un item sin nombre en la lista).
        Assert.Equal("raw_page_042", item.Title);
        Assert.Null(item.Duration);
        Assert.Equal("https://example.com/nota-con-audio", item.Url);
    }

    [Fact]
    public void Parse_json_vacio_lanza_YtDlpParseException_de_NoAudioFound()
    {
        var ex = Assert.Throws<YtDlpParseException>(() => YtDlpJsonParser.Parse("{}"));

        Assert.Equal(WebImportStatus.NoAudioFound, ex.Status);
        Assert.Equal("No se encontró audio en esa página.", ex.Message);
    }

    [Fact]
    public void Parse_playlist_sin_entradas_lanza_YtDlpParseException_de_NoAudioFound()
    {
        var ex = Assert.Throws<YtDlpParseException>(
            () => YtDlpJsonParser.Parse("""{ "_type": "playlist", "title": "Vacía", "entries": [] }"""));

        Assert.Equal(WebImportStatus.NoAudioFound, ex.Status);
    }

    [Fact]
    public void Parse_texto_que_no_es_json_lanza_YtDlpParseException()
    {
        var ex = Assert.Throws<YtDlpParseException>(() => YtDlpJsonParser.Parse("no soy json"));

        Assert.Equal(WebImportStatus.YtDlpFailed, ex.Status);
    }

    [Fact]
    public void Parse_string_vacio_o_en_blanco_lanza_YtDlpParseException_de_NoAudioFound()
    {
        var ex = Assert.Throws<YtDlpParseException>(() => YtDlpJsonParser.Parse("   "));

        Assert.Equal(WebImportStatus.NoAudioFound, ex.Status);
    }
}
