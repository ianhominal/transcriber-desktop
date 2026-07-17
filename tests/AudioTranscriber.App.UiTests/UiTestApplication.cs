using System.Windows;
using System.Windows.Threading;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Bootstrap mínimo y compartido de System.Windows.Application para los tests de este proyecto.
/// Sin una Application viva, los StaticResource (Border, Accent, Card, etc. definidos en
/// Styles/*.xaml y mergeados en App.xaml) no resuelven, y cualquier ventana/UserControl que los use
/// revienta con "Cannot find resource named ...". <see cref="AudioTranscriber.App.App"/>.
/// InitializeComponent() (generado por el compilador de XAML desde App.xaml) carga exactamente los
/// mismos ResourceDictionary que usa la app real — pero acá NUNCA se llama a Run(), así que
/// StartupUri no crea/muestra MainWindow ni corre OnStartup (que arrancaría TrayIconService,
/// UpdateService, etc., cosas que no queremos en un test).
/// </summary>
internal static class UiTestApplication
{
    public static void EnsureCreated()
    {
        if (Application.Current is not null)
            return;

        var app = new App();
        app.InitializeComponent();
    }

    /// <summary>
    /// Procesa la cola del Dispatcher hasta que quede libre (equivalente a un "DoEvents"). El
    /// evento Loaded de WPF se dispara de forma asincrónica vía el Dispatcher: para reproducir de
    /// forma confiable en un test el mismo timing que ve el usuario real (donde el crash original
    /// de esta tarea ocurría durante el arranque, antes de que la ventana termine de mostrarse),
    /// hace falta bombear el Dispatcher después de Show() en vez de asumir que Loaded ya corrió.
    /// </summary>
    public static void PumpDispatcherUntilIdle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
