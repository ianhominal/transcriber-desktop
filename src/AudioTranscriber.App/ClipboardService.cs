using System.Windows;
using AudioTranscriber.Core.Export;

namespace AudioTranscriber.App;

/// <summary>
/// Copia texto al portapapeles en DOS formatos a la vez: texto plano (siempre funciona, cualquier
/// editor) + <c>CF_HTML</c> (ver <see cref="ClipboardHtmlBuilder"/> en Core, lógica pura y
/// testeada) para que pegar en Word/Google Docs/Gmail conserve estructura básica (párrafos) en vez
/// de perder todo el formato -- mismo criterio que ya usa la web (copia rich-text para pegar en
/// Docs/Word, ver brief "Copiar con formato"). Esta clase es la ÚNICA que toca
/// <see cref="Clipboard"/> directo (capa WPF); el armado del HTML vive en Core para poder testearlo
/// sin STA thread.
/// </summary>
public static class ClipboardService
{
    public static void CopyPlainAndHtml(string? plainText, string? htmlFragment)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, plainText ?? string.Empty);
        data.SetData(DataFormats.Html, ClipboardHtmlBuilder.BuildCfHtml(htmlFragment ?? string.Empty));
        // copy: true -- el contenido sigue disponible después de que la app cierre (mismo criterio
        // que Clipboard.SetText, que ya lo hace por default).
        Clipboard.SetDataObject(data, true);
    }
}
