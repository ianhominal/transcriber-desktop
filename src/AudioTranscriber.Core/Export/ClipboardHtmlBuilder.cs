using System.Text;

namespace AudioTranscriber.Core.Export;

/// <summary>
/// Arma el fragmento CF_HTML que Windows espera en el portapapeles para que pegar en Word/Google
/// Docs/Gmail conserve estructura básica (párrafos) en vez de perder todo el formato -- mismo
/// criterio que ya usa la web (copia rich-text para pegar en Docs/Word, ver brief "Copiar con
/// formato"). Lógica PURA de armado de strings (sin tocar <c>System.Windows.Clipboard</c> -- eso
/// vive en <c>AudioTranscriber.App.ClipboardService</c>, capa WPF), así que es testeable acá en
/// Core sin necesitar una ventana ni un STA thread.
/// </summary>
public static class ClipboardHtmlBuilder
{
    /// <summary>
    /// Convierte texto plano a un fragmento HTML simple: párrafos separados por línea en blanco
    /// (<c>&lt;p&gt;</c>), saltos de línea sueltos dentro de un párrafo como <c>&lt;br&gt;</c>.
    /// Escapa entidades HTML (&amp;, &lt;, &gt;) -- nunca inyecta el texto crudo como markup.
    /// </summary>
    public static string TextToHtmlFragment(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n");
        var paragraphs = normalized.Split("\n\n", StringSplitOptions.None);

        var sb = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.Trim('\n');
            if (trimmed.Length == 0)
                continue;
            var escaped = EscapeHtml(trimmed).Replace("\n", "<br>");
            sb.Append("<p>").Append(escaped).Append("</p>");
        }
        return sb.ToString();
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Envuelve <paramref name="htmlFragment"/> (el contenido real, p.ej. de
    /// <see cref="TextToHtmlFragment"/>) en el formato CF_HTML completo con el header de offsets que
    /// exige la especificación de Windows ("HTML Clipboard Format", Version 1.0):
    /// <c>Version</c>/<c>StartHTML</c>/<c>EndHTML</c>/<c>StartFragment</c>/<c>EndFragment</c>, todos
    /// offsets en BYTES (UTF-8) medidos desde el inicio del string completo devuelto. Los cuatro
    /// números se formatean siempre a 10 dígitos (<c>0000000000</c>), así que el header tiene
    /// longitud FIJA sin importar el valor real -- alcanza una sola pasada (sin iterar) para calcular
    /// los offsets correctos.
    /// </summary>
    public static string BuildCfHtml(string htmlFragment)
    {
        const string headerFormat =
            "Version:0.9\r\n" +
            "StartHTML:{0:0000000000}\r\n" +
            "EndHTML:{1:0000000000}\r\n" +
            "StartFragment:{2:0000000000}\r\n" +
            "EndFragment:{3:0000000000}\r\n";
        const string htmlPrefix = "<html><body>\r\n<!--StartFragment-->";
        const string htmlSuffix = "<!--EndFragment-->\r\n</body></html>";

        // Header de longitud fija (padding a 10 dígitos): un placeholder con ceros ya tiene el
        // largo final exacto, no hace falta una segunda pasada.
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(headerFormat, 0, 0, 0, 0));
        var prefixLength = Encoding.UTF8.GetByteCount(htmlPrefix);
        var fragmentLength = Encoding.UTF8.GetByteCount(htmlFragment);
        var suffixLength = Encoding.UTF8.GetByteCount(htmlSuffix);

        var startHtml = headerLength;
        var startFragment = startHtml + prefixLength;
        var endFragment = startFragment + fragmentLength;
        var endHtml = endFragment + suffixLength;

        var header = string.Format(headerFormat, startHtml, endHtml, startFragment, endFragment);
        return header + htmlPrefix + htmlFragment + htmlSuffix;
    }
}
