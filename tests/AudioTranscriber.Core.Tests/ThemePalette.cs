using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Lee una paleta REAL (Colors.Dark.xaml / Colors.Light.xaml) desde el disco.
///
/// Lee el archivo de verdad, no una copia: el objetivo es que los tests de contraste midan lo que
/// la app pinta. Una constante duplicada en el test se desincroniza y el test pasa a mentir — que
/// es exactamente el problema que estos tests existen para matar (la regla vivía en un comentario
/// y nada la verificaba).
/// </summary>
public sealed class ThemePalette
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private readonly Dictionary<string, string> _hex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _opacity = new(StringComparer.Ordinal);

    public string Name { get; }

    private ThemePalette(string name) => Name = name;

    public static ThemePalette Dark() => Load("Colors.Dark.xaml", "dark");
    public static ThemePalette Light() => Load("Colors.Light.xaml", "light");

    /// <summary>Las dos paletas, para los tests que corren sobre ambas.</summary>
    public static IEnumerable<ThemePalette> Both()
    {
        yield return Dark();
        yield return Light();
    }

    private static ThemePalette Load(string fileName, string name)
    {
        var path = Path.Combine(StylesDirectory(), fileName);
        var doc = XDocument.Load(path);
        var palette = new ThemePalette(name);

        // <Color x:Key="AccentColor">#6366F1</Color>
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Color" && e.Attribute(X + "Key") is not null))
            palette._hex[el.Attribute(X + "Key")!.Value] = el.Value.Trim();

        // <SolidColorBrush x:Key="Accent" Color="#RRGGBB" Opacity="0.55"/>
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "SolidColorBrush"))
        {
            var key = el.Attribute(X + "Key")?.Value;
            var color = el.Attribute("Color")?.Value?.Trim();
            if (key is null || color is null) continue;

            // Color="{StaticResource AccentColor}" -> resolver contra los <Color> de arriba.
            if (color.StartsWith('{'))
            {
                var referenced = color.Trim('{', '}').Split(' ').Last().Trim();
                if (!palette._hex.TryGetValue(referenced, out var resolved))
                    throw new InvalidOperationException($"{fileName}: '{key}' referencia '{referenced}', que no existe.");
                color = resolved;
            }

            palette._hex[key] = color;

            var opacity = el.Attribute("Opacity")?.Value;
            if (opacity is not null)
                palette._opacity[key] = double.Parse(opacity, System.Globalization.CultureInfo.InvariantCulture);
        }

        return palette;
    }

    /// <summary>Valor hex de una clave. Tira si no existe — una clave faltante en UN tema es el bug
    /// silencioso que documenta el comentario de ThemeManager: DynamicResource no resuelve, WPF no
    /// tira excepción, y el control hereda el color del padre.</summary>
    public string Hex(string key) =>
        _hex.TryGetValue(key, out var v)
            ? v
            : throw new KeyNotFoundException($"La paleta '{Name}' no define '{key}'.");

    public bool Has(string key) => _hex.ContainsKey(key);

    /// <summary>Opacity declarada (1.0 si el brush no la declara).</summary>
    public double Opacity(string key) => _opacity.TryGetValue(key, out var v) ? v : 1.0;

    public IReadOnlyCollection<string> Keys => _hex.Keys;

    /// <summary>Ubica Styles/ subiendo desde el binario de test hasta encontrar el repo.</summary>
    private static string StylesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AudioTranscriber.App", "Styles");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"No se encontró src/AudioTranscriber.App/Styles subiendo desde {AppContext.BaseDirectory}.");
    }
}
