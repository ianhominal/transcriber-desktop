using System.Windows;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Cobertura amplia (project-wide) de latentes errores runtime en Styles/*.xaml: instancia
/// controles reales que consumen cada Style/ControlTemplate del sistema de diseño y fuerza su
/// render, en vez de asumir que "si compiló, está bien" (el bug de esta tarea demuestra que XAML
/// puede compilar perfecto y crashear recién en runtime).
/// <para/>
/// El cuerpo corre marshaleado a <see cref="UiThread"/> (ver ese archivo para el porqué) en vez de
/// usar [StaFact] directo.
/// </summary>
public class ResourceDictionariesUiTests
{
    [Fact]
    public void App_resources_load_and_common_styled_controls_render_without_throwing() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            // Si App.InitializeComponent() (llamado en UiTestApplication.EnsureCreated) no reventó,
            // Colors/Typography/Buttons/Inputs/Controls.xaml ya se cargaron y mergearon en
            // Application.Resources. Lo confirmamos explícitamente acá.
            Assert.NotNull(Application.Current);
            Assert.True(Application.Current!.Resources.MergedDictionaries.Count > 0);
            Assert.NotNull(Application.Current.TryFindResource("Card"));
            Assert.NotNull(Application.Current.TryFindResource("ToggleSwitch"));
            Assert.NotNull(Application.Current.TryFindResource("TreeViewItemFlat"));

            var exception = Record.Exception(() =>
            {
                var panel = new System.Windows.Controls.StackPanel();

                var card = new System.Windows.Controls.Border { Style = (Style)Application.Current.FindResource("Card") };
                var toggle = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Style = (Style)Application.Current.FindResource("ToggleSwitch"),
                };
                var expander = new System.Windows.Controls.Expander { Header = "test" };
                var slider = new System.Windows.Controls.Slider();

                panel.Children.Add(card);
                panel.Children.Add(toggle);
                panel.Children.Add(expander);
                panel.Children.Add(slider);

                var window = new Window
                {
                    Content = panel,
                    Width = 200,
                    Height = 200,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                };

                window.Show();
                UiTestApplication.PumpDispatcherUntilIdle();
                window.UpdateLayout();
                window.Close();
            });

            Assert.Null(exception);
        });
}
