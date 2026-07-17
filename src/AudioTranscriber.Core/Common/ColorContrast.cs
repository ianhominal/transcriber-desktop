using System;
using System.Globalization;

namespace AudioTranscriber.Core.Common;

/// <summary>
/// Calcula el ratio de contraste entre dos colores, según la fórmula de WCAG 2.1.
///
/// Existe por una razón concreta: la paleta de esta app tenía la regla correcta escrita en un
/// comentario ("dark = shade 400 de Tailwind", "un anillo de foco VISIBLE") y nada que la hiciera
/// cumplir. Resultado medido el 2026-07-16: el acento como texto daba 2.94:1, el anillo de foco
/// 1.96:1 (o sea, invisible), y los botones deshabilitados 1.82:1. Todo eso entró sin ruido porque
/// ningún test miraba los valores reales.
///
/// Un comentario no verifica nada. Esta clase + los tests que la usan sí.
///
/// Umbrales de WCAG 2.1 AA:
///   4.5:1 para texto normal · 3:1 para texto grande (>=18.66pt bold o >=24px) y para componentes
///   de interfaz (bordes de foco, íconos que transmiten información).
/// </summary>
public static class ColorContrast
{
    /// <summary>Mínimo de WCAG AA para texto normal.</summary>
    public const double AaNormalText = 4.5;

    /// <summary>Mínimo de WCAG AA para texto grande y para componentes de interfaz (foco, bordes).</summary>
    public const double AaLargeTextOrUi = 3.0;

    /// <summary>
    /// Ratio de contraste entre dos colores hex ("#RRGGBB" o "#AARRGGBB"). Va de 1.0 (idénticos) a
    /// 21.0 (negro contra blanco). El orden de los argumentos no importa.
    /// </summary>
    public static double Ratio(string hex1, string hex2)
    {
        var l1 = RelativeLuminance(hex1);
        var l2 = RelativeLuminance(hex2);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Ratio de un color con transparencia (Opacity de WPF) compuesto sobre un fondo.
    ///
    /// Hace falta porque WPF aplica Opacity al elemento entero: lo que el ojo ve NO es el color
    /// declarado, es la mezcla contra lo que hay atrás. El anillo de foco declaraba el acento —
    /// que sobre papel contrasta bien — pero a 0.55 de opacidad terminaba en 1.96:1 contra la
    /// superficie. El color "correcto" con opacidad puede ser invisible.
    /// </summary>
    public static double RatioWithOpacity(string foregroundHex, double opacity, string backgroundHex)
    {
        if (opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));

        var (fr, fg, fb) = ToRgb(foregroundHex);
        var (br, bg, bb) = ToRgb(backgroundHex);

        // Composición alpha estándar sobre los valores de 8 bits (que es lo que hace el compositor).
        var r = fr * opacity + br * (1 - opacity);
        var g = fg * opacity + bg * (1 - opacity);
        var b = fb * opacity + bb * (1 - opacity);

        var lf = Luminance(r, g, b);
        var lb = RelativeLuminance(backgroundHex);
        var lighter = Math.Max(lf, lb);
        var darker = Math.Min(lf, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Luminancia relativa (WCAG) de un color hex.</summary>
    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ToRgb(hex);
        return Luminance(r, g, b);
    }

    private static double Luminance(double r8, double g8, double b8) =>
        0.2126 * Channel(r8) + 0.7152 * Channel(g8) + 0.0722 * Channel(b8);

    /// <summary>Linealiza un canal de 8 bits, tal cual lo define WCAG 2.1.</summary>
    private static double Channel(double value8Bit)
    {
        var c = value8Bit / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>Parsea "#RRGGBB" o "#AARRGGBB" (el alfa se ignora: para eso está RatioWithOpacity).</summary>
    public static (int R, int G, int B) ToRgb(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) throw new ArgumentException("Color vacío.", nameof(hex));

        var h = hex.Trim().TrimStart('#');
        if (h.Length == 8) h = h[2..]; // #AARRGGBB -> RRGGBB
        if (h.Length != 6) throw new FormatException($"Color no soportado: '{hex}'. Se espera #RRGGBB o #AARRGGBB.");

        return (
            int.Parse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        );
    }
}
