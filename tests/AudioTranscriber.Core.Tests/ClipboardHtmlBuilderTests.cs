using System.Text;
using AudioTranscriber.Core.Export;

namespace AudioTranscriber.Core.Tests;

public class ClipboardHtmlBuilderTests
{
    // ---- TextToHtmlFragment ------------------------------------------------------------

    [Fact]
    public void TextToHtmlFragment_UnParrafo_LoEnvuelveEnP()
    {
        Assert.Equal("<p>Hola mundo</p>", ClipboardHtmlBuilder.TextToHtmlFragment("Hola mundo"));
    }

    [Fact]
    public void TextToHtmlFragment_DosParrafosSeparadosPorLineaEnBlanco_GeneraDosP()
    {
        var html = ClipboardHtmlBuilder.TextToHtmlFragment("Primer párrafo.\n\nSegundo párrafo.");

        Assert.Equal("<p>Primer párrafo.</p><p>Segundo párrafo.</p>", html);
    }

    [Fact]
    public void TextToHtmlFragment_SaltoDeLineaSuelto_SeConvierteEnBr()
    {
        var html = ClipboardHtmlBuilder.TextToHtmlFragment("Línea 1\nLínea 2");

        Assert.Equal("<p>Línea 1<br>Línea 2</p>", html);
    }

    [Fact]
    public void TextToHtmlFragment_EscapaEntidadesHtml()
    {
        var html = ClipboardHtmlBuilder.TextToHtmlFragment("<script>alert('x')</script> & cía");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp; cía", html);
    }

    [Fact]
    public void TextToHtmlFragment_VacioONulo_DevuelveVacio()
    {
        Assert.Equal(string.Empty, ClipboardHtmlBuilder.TextToHtmlFragment(""));
        Assert.Equal(string.Empty, ClipboardHtmlBuilder.TextToHtmlFragment(null));
    }

    // ---- BuildCfHtml ---------------------------------------------------------------------

    [Fact]
    public void BuildCfHtml_IncluyeElHeaderVersion()
    {
        var cfHtml = ClipboardHtmlBuilder.BuildCfHtml("<p>hola</p>");

        Assert.StartsWith("Version:0.9\r\n", cfHtml);
        Assert.Contains("StartHTML:", cfHtml);
        Assert.Contains("EndHTML:", cfHtml);
        Assert.Contains("StartFragment:", cfHtml);
        Assert.Contains("EndFragment:", cfHtml);
    }

    [Fact]
    public void BuildCfHtml_LosOffsetsApuntanExactamenteAlFragmento()
    {
        const string fragment = "<p>hola, ¿cómo va?</p>";
        var cfHtml = ClipboardHtmlBuilder.BuildCfHtml(fragment);

        var (startFragment, endFragment) = ReadFragmentOffsets(cfHtml);
        var bytes = Encoding.UTF8.GetBytes(cfHtml);
        var sliced = Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment);

        Assert.Equal(fragment, sliced);
    }

    [Fact]
    public void BuildCfHtml_StartHtmlYEndHtmlApuntanAlDocumentoCompleto()
    {
        const string fragment = "<p>texto</p>";
        var cfHtml = ClipboardHtmlBuilder.BuildCfHtml(fragment);

        var startHtml = ReadOffset(cfHtml, "StartHTML");
        var endHtml = ReadOffset(cfHtml, "EndHTML");
        var bytes = Encoding.UTF8.GetBytes(cfHtml);

        Assert.Equal(bytes.Length, endHtml);

        var htmlPart = Encoding.UTF8.GetString(bytes, startHtml, endHtml - startHtml);
        Assert.StartsWith("<html><body>", htmlPart);
        Assert.Contains("<!--StartFragment-->", htmlPart);
        Assert.Contains("<!--EndFragment-->", htmlPart);
        Assert.EndsWith("</body></html>", htmlPart);
    }

    [Fact]
    public void BuildCfHtml_FragmentoConAcentos_OffsetsSiguenSiendoCorrectos()
    {
        // Español con tildes/eñes fuerza bytes multi-byte en UTF-8 -- el cálculo de offsets tiene
        // que contar BYTES, no caracteres, o el slice de abajo quedaría corrido.
        const string fragment = "<p>Reunión con el equipo: mañana a las 9hs, ¡no llegues tarde!</p>";
        var cfHtml = ClipboardHtmlBuilder.BuildCfHtml(fragment);

        var (startFragment, endFragment) = ReadFragmentOffsets(cfHtml);
        var bytes = Encoding.UTF8.GetBytes(cfHtml);
        var sliced = Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment);

        Assert.Equal(fragment, sliced);
    }

    [Fact]
    public void BuildCfHtml_FragmentoVacio_NoLanzaYOffsetsCoinciden()
    {
        var cfHtml = ClipboardHtmlBuilder.BuildCfHtml(string.Empty);

        var (startFragment, endFragment) = ReadFragmentOffsets(cfHtml);
        Assert.Equal(startFragment, endFragment);
    }

    private static (int Start, int End) ReadFragmentOffsets(string cfHtml) =>
        (ReadOffset(cfHtml, "StartFragment"), ReadOffset(cfHtml, "EndFragment"));

    private static int ReadOffset(string cfHtml, string key)
    {
        var marker = key + ":";
        var start = cfHtml.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = cfHtml.IndexOf('\r', start);
        return int.Parse(cfHtml[start..end]);
    }
}
