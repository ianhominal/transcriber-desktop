using System.Xml.Linq;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Prevención de regresión de un bug real (crash al abrir la app): un Freezable (RotateTransform,
/// SolidColorBrush, DropShadowEffect, etc.) nombrado con x:Name DENTRO de una property-element
/// (ej. &lt;Ellipse.RenderTransform&gt;&lt;RotateTransform x:Name="Rot".../&gt;&lt;/Ellipse.RenderTransform&gt;)
/// NO se registra en el NameScope del ControlTemplate/Window. Un Storyboard.TargetName que apunte
/// a ese nombre revienta en runtime con: "El nombre 'X' no se encuentra en el ámbito de nombres de
/// System.Windows.Controls.ControlTemplate". Ver Styles/Controls.xaml (estilo "Spinner"), donde
/// ocurrió exactamente esto.
///
/// Este proyecto de test es net8.0 puro (sin UseWPF, sin referencia a AudioTranscriber.App) para
/// mantenerlo liviano y multiplataforma, así que este test NO carga WPF real: parsea los .xaml
/// como XML plano y detecta el patrón por estructura + una lista de tipos Freezable conocidos. No
/// reemplaza una prueba de instanciación real, pero atrapa en CI la clase de bug que causó el
/// crash, cosa que antes solo se veía en el runtime del usuario.
/// </summary>
public class XamlNameScopeAntiPatternTests
{
    // Tipos Freezable comunes (Transform/Brush/Effect/Geometry) que NO son FrameworkElement: si se
    // nombran dentro de una property-element, x:Name no se registra en el NameScope del template.
    private static readonly HashSet<string> KnownFreezableTypeNames = new(StringComparer.Ordinal)
    {
        // Transforms
        "RotateTransform", "ScaleTransform", "TranslateTransform", "SkewTransform",
        "TransformGroup", "MatrixTransform",
        // Effects
        "DropShadowEffect", "BlurEffect", "ShaderEffect",
        // Brushes
        "SolidColorBrush", "LinearGradientBrush", "RadialGradientBrush", "ImageBrush",
        "VisualBrush", "DrawingBrush", "GradientStop",
        // Geometries
        "PathGeometry", "RectangleGeometry", "EllipseGeometry", "LineGeometry",
        "GeometryGroup", "StreamGeometry", "CombinedGeometry",
        // Otros Freezables comunes
        "Pen", "BitmapImage", "DoubleCollection", "PointCollection",
    };

    public static IEnumerable<object[]> AppXamlFiles()
        => GetAppXamlFiles().Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(AppXamlFiles))]
    public void Xaml_no_anima_por_storyboard_un_freezable_nombrado_fuera_del_namescope(string xamlPath)
    {
        var doc = XDocument.Load(xamlPath);
        var xNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        // 1. Nombres "fantasma": x:Name sobre un Freezable conocido cuyo padre inmediato es una
        //    property-element (LocalName con un punto, ej. "Ellipse.RenderTransform").
        var unregisteredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in doc.Descendants())
        {
            var name = el.Attribute(xNs + "Name")?.Value;
            if (name is null || !KnownFreezableTypeNames.Contains(el.Name.LocalName))
                continue;

            if (el.Parent is { } parent && parent.Name.LocalName.Contains('.'))
                unregisteredNames.Add(name);
        }

        if (unregisteredNames.Count == 0)
            return; // nada riesgoso nombrado en este archivo.

        // 2. ¿Algún Storyboard.TargetName (o TargetName de una animación) apunta a esos nombres?
        var offending = doc.Descendants()
            .Select(el => el.Attribute("Storyboard.TargetName") ?? el.Attribute("TargetName"))
            .Where(attr => attr is not null && unregisteredNames.Contains(attr.Value))
            .Select(attr => attr!.Value)
            .Distinct()
            .ToList();

        Assert.True(offending.Count == 0,
            $"'{Path.GetFileName(xamlPath)}' tiene Storyboard.TargetName apuntando a " +
            $"[{string.Join(", ", offending)}], pero ese nombre pertenece a un Freezable " +
            "(Transform/Brush/Effect/Geometry) nombrado dentro de una property-element (ej. " +
            "Ellipse.RenderTransform). Ese nombre NO se registra en el NameScope de WPF y revienta " +
            "en runtime con \"El nombre 'X' no se encuentra en el ámbito de nombres...\". Nombrá el " +
            "FrameworkElement contenedor (ej. la Ellipse) y animá con Storyboard.TargetProperty=" +
            "\"(UIElement.RenderTransform).(RotateTransform.Angle)\" en su lugar.");
    }

    private static IEnumerable<string> GetAppXamlFiles()
    {
        var appDir = FindAppProjectDirectory();
        return Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static string FindAppProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "AudioTranscriber.App");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "No se encontró 'src/AudioTranscriber.App' subiendo desde " + AppContext.BaseDirectory +
            ". Este test necesita correrse dentro del repo completo de AudioTranscriber.");
    }
}
