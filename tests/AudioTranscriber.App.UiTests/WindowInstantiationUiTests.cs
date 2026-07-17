using System.Net.Http;
using System.Windows;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Instancia ventanas reales de la app (no solo el Spinner aislado) para ampliar la cobertura de
/// regresión de XAML runtime: si alguna ventana rompe algún recurso compartido (Styles/*.xaml) o
/// cualquier otro patrón de NameScope, esto lo agarra. MainViewModel/LoginWindow hacen algo de I/O
/// en su constructor (settings persistidos en %LOCALAPPDATA%, igual que en la app real) pero no
/// hacen llamadas de red en el constructor, así que son seguras de instanciar en un test.
/// <para/>
/// El cuerpo corre marshaleado a <see cref="UiThread"/> (ver ese archivo para el porqué) en vez de
/// usar [StaFact] directo.
/// </summary>
public class WindowInstantiationUiTests
{
    [Fact]
    public void MainWindow_instantiates_shows_and_renders_without_throwing() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            var exception = Record.Exception(() =>
            {
                var window = new MainWindow
                {
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -3000,
                    Top = -3000,
                };

                window.Show();
                UiTestApplication.PumpDispatcherUntilIdle();
                window.UpdateLayout();
                window.Close();
            });

            Assert.Null(exception);
        });

    [Fact]
    public void LoginWindow_instantiates_shows_and_renders_without_throwing() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            var exception = Record.Exception(() =>
            {
                using var http = new HttpClient();
                var window = new LoginWindow(http)
                {
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -3000,
                    Top = -3000,
                };

                window.Show();
                UiTestApplication.PumpDispatcherUntilIdle();
                window.UpdateLayout();
                window.Close();
            });

            Assert.Null(exception);
        });
}
