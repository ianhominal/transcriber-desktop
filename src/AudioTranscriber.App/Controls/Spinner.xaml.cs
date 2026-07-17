using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AudioTranscriber.App.Controls;

/// <summary>
/// Spinner giratorio. La animación se arranca por CODE-BEHIND en <see cref="OnLoaded"/> (no por
/// Storyboard.TargetName en un ControlTemplate, que crasheaba en runtime con "El nombre 'Arc' no se
/// encuentra en el ámbito de nombres de ControlTemplate"). El RotateTransform tiene x:Name en el
/// XAML → InitializeComponent genera el campo <c>ArcRotate</c> accesible directo desde acá, sin
/// depender de ningún NameScope de template, así que no puede fallar por resolución de nombres.
/// </summary>
public partial class Spinner : UserControl
{
    public Spinner()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        ArcRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Detener la animación al descargar el control libera el reloj de animación.
        ArcRotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }
}
