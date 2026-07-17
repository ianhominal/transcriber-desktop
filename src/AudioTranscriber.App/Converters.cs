using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioTranscriber.App;

/// <summary>bool -> Visibility (true = Visible, false = Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool -> Visibility invertido (true = Collapsed, false = Visible).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>bool -> bool invertido -- para <c>IsEnabled</c> bindeado a un flag de "ocupado" (ej.
/// <c>IsChatBusy</c> en el input del chat), a diferencia de <see cref="InverseBoolToVisibilityConverter"/>
/// que devuelve <see cref="Visibility"/> y no sirve para propiedades bool.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>
/// URL (string) -> BitmapImage, para el avatar de Google en SyncWindow. A diferencia de bindear
/// un Image.Source directo contra un string (que usa el TypeConverter default de WPF), acá se
/// controla a mano para: (1) devolver null en vacío en vez de tirar una UriFormatException al
/// intentar parsear "" como URI, y (2) devolver null (no tirar) ante cualquier URL inválida o un
/// avatar que no cargue, así el DataTrigger de "sin avatar" en SyncWindow.xaml cae al placeholder
/// de iniciales en vez de dejar un ícono roto.
/// </summary>
public sealed class AvatarUrlToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch
        {
            return null; // URL inválida: la UI cae al placeholder de iniciales.
        }
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Colección vacía (o null) -> Visibility.Visible; con elementos -> Collapsed. Puramente visual:
/// se usa para mostrar el "empty state" del panel de Proyectos ("Todavía no hay proyectos") sin
/// tocar el ViewModel. Con parameter="Invert" el resultado se invierte (útil para mostrar el
/// contenido normal solo cuando SÍ hay elementos).
/// También acepta directamente un int (ej. binding a "Coleccion.Count"): 0 -> vacío. IMPORTANTE:
/// bindear a la colección misma (ej. "{Binding Projects}") NO alcanza cuando la colección es una
/// propiedad get-only (ObservableCollection) que se puebla con Clear()/Add() en vez de
/// reasignarse: WPF evalúa esa Binding una sola vez porque nunca se dispara PropertyChanged sobre
/// la propiedad "Projects" (ObservableCollection solo notifica CollectionChanged). El binding
/// correcto es a "Projects.Count", que SÍ se re-evalúa porque ObservableCollection notifica
/// PropertyChanged("Count") en cada Add/Remove/Clear.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value switch
        {
            null => true,
            int count => count == 0,
            ICollection collection => collection.Count == 0,
            IEnumerable enumerable => !enumerable.GetEnumerator().MoveNext(),
            _ => true
        };

        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        if (invert)
            isEmpty = !isEmpty;

        return isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Id de color de proyecto (ver <see cref="AudioTranscriber.Core.Workspaces.ProjectColorPalette"/>)
/// -> Brush, para pintar el punto y el borde de acento del árbol de Proyectos (ver
/// MainWindow.xaml, HierarchicalDataTemplate de ProjectVm) y el anillo de selección del picker de
/// colores (ver ProjectInfoDialog.xaml). Resuelve contra los brushes
/// ProjectRed/ProjectOrange/.../ProjectRose ya mergeados en Application.Current.Resources
/// (Colors.Light.xaml/Colors.Dark.xaml), el mismo mecanismo de theme-swap que usa el resto de la
/// paleta semántica (ver ThemeManager) — así el color de acento cambia de shade en vivo al cambiar
/// de tema, sin código extra acá.
/// <para/>
/// <c>null</c>, string vacío, o cualquier id que no matchee una clave conocida resuelven a
/// <see cref="Brushes.Transparent"/> — nunca tira. El color es siempre decorativo (punto/borde,
/// nunca un fondo detrás del texto): un id inválido simplemente no pinta nada, no rompe la fila.
/// </summary>
public sealed class ProjectColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string id || string.IsNullOrWhiteSpace(id))
            return Brushes.Transparent;

        var key = "Project" + char.ToUpperInvariant(id[0]) + id[1..];
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
