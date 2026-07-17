using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioTranscriber.App.Controls;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Prueba de instanciación WPF REAL (no un parser de XML) del Spinner. Ver
/// XamlNameScopeAntiPatternTests (AudioTranscriber.Core.Tests) para contexto: ese test es
/// puramente estructural (parsea XAML como XML plano, sin WPF) y NO detecta el bug real que motivó
/// este proyecto — el Style + ControlTemplate + Storyboard.TargetName de la versión vieja seguía
/// crasheando en runtime incluso después de "arreglar" el anti-patrón clásico de
/// Freezable-nombrado-en-property-element (nombrar el Ellipse en vez del RotateTransform). Este
/// test SÍ instancia WPF real (Window + Loaded + render pass), así que si el bug se reintroduce,
/// FALLA. Ver el reporte de esta tarea para el before/after real que lo demuestra.
/// <para/>
/// El cuerpo corre marshaleado a <see cref="UiThread"/> (ver ese archivo para el porqué) en vez de
/// usar [StaFact] directo.
/// </summary>
public class SpinnerUiTests
{
    [Fact]
    public void Spinner_instantiates_shows_fires_loaded_and_renders_without_throwing() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            var exception = Record.Exception(() =>
            {
                var spinner = new Spinner();
                var window = new Window
                {
                    Content = spinner,
                    Width = 100,
                    Height = 100,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -2000,
                    Top = -2000,
                };

                // Nota: NO se llama a window.Measure()/Arrange() manualmente — un Window es un
                // FrameworkElement respaldado por un HWND nativo y gestiona su propio layout vía
                // Show()/UpdateLayout(); invocar Measure/Arrange a mano sobre él termina en un
                // Environment.FailFast nativo dentro de Window.GetWindowMinMax() (crash irrecuperable
                // del proceso, no una excepción .NET atrapable) — se comprobó al escribir este test.
                window.Show();
                UiTestApplication.PumpDispatcherUntilIdle();
                spinner.ApplyTemplate();
                window.UpdateLayout();

                // Fuerza un render pass real: el crash original ("El nombre 'Arc' no se encuentra en
                // el ámbito de nombres...") se dispara desde el motor de animación/composición al
                // resolver el Storyboard.TargetName, así que un render pass es necesario para
                // reproducirlo.
                var bitmap = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);

                window.Close();
            });

            Assert.Null(exception);
        });
}
