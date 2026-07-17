using System.Reflection;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Regresión de dos bugs reales de TrayIconService, ambos confirmados con evidencia real (stack
/// traces capturados en %LOCALAPPDATA%\AudioTranscriber\logs\crash-*.log de una instalación real,
/// no solo hipótesis):
/// <list type="bullet">
/// <item>Ícono de bandeja cargado con pack URI (pack://application:,,,/appicon.ico), que depende de
/// Assembly.Location — vacío cuando la app se publica con PublishSingleFile=true (el publish real
/// de este proyecto). Crasheaba el arranque con "No se encuentra el recurso 'appicon.ico'." Fix:
/// ver <see cref="TrayIconService"/> (EmbeddedResource + Assembly.GetManifestResourceStream, que no
/// depende de Location) — LoadTrayIcon en ese archivo.</item>
/// <item>Acceso cross-thread a UI: <c>UpdateStatusHeader</c> tocaba un DependencyObject
/// (MenuItem.Header) directamente desde <c>OnSyncCoordinatorPropertyChanged</c>, que puede
/// dispararse desde un hilo que NO es el de UI (SyncCoordinator.RunSyncAsync corre async y algún
/// await en esa cadena no vuelve al SynchronizationContext de WPF). Crash real: "El subproceso que
/// realiza la llamada no puede obtener acceso a este objeto porque el propietario es otro
/// subproceso.", con TrayIconService.UpdateStatusHeader ← OnSyncCoordinatorPropertyChanged ←
/// SyncCoordinator.set_IsBusy ← RunSyncAsync en la pila. Mismo riesgo documentado explícitamente en
/// UpdateService.UpdateReady ("el caller es responsable de marshalear a UI") — OnUpdateReady
/// tampoco lo hacía. Fix: ambos métodos ahora marshalean vía Dispatcher.CheckAccess()/BeginInvoke.</item>
/// </list>
/// Todos los tests de este archivo corren marshaleados a <see cref="UiThread"/> (ver ese archivo
/// para el porqué: TrayIconService toca Application.Current -- vía RequestExit/OpenApp y, antes,
/// el ahora reemplazado TaskbarIcon..ctor -- afín al thread que creó la Application, así que no
/// puede correr en un thread STA nuevo por test como haría [StaFact] directo).
/// <para/>
/// Marcados con <c>[Trait("Category", "RealTrayIcon")]</c>: construir un <see cref="TrayIconService"/>
/// registra un ícono REAL en la bandeja de Windows (NotifyIcon.Visible=true) en la máquina de quien
/// corre los tests -- un efecto secundario visible. Se los puede excluir de un run por default con
/// <c>dotnet test --filter "Category!=RealTrayIcon"</c>. El globo/toast audible que además disparaba
/// el flujo de "actualización lista" YA se eliminó de raíz con el seam <see cref="ITrayNotifier"/>
/// (ver el fake de abajo), así que estos tests ya no hacen ruido; el Trait cubre el ícono en sí.
/// </summary>
[Trait("Category", "RealTrayIcon")]
public class TrayIconServiceUiTests
{
    /// <summary>
    /// Fake de <see cref="ITrayNotifier"/> que solo REGISTRA las notificaciones pedidas, sin
    /// disparar el globo real de WinForms -- así el test de "actualización lista" verifica el flujo
    /// sin sonido ni toast en la máquina.
    /// </summary>
    private sealed class RecordingTrayNotifier : ITrayNotifier
    {
        public List<(string Title, string Message)> Balloons { get; } = new();

        public void ShowBalloon(string title, string message) => Balloons.Add((title, message));
    }

    [Fact]
    public void TrayIconService_instantiates_and_disposes_cleanly() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            var window = NewOffscreenWindow(-5000);
            window.Show();

            TrayIconService? service = null;
            var exception = Record.Exception(() => service = new TrayIconService(window));

            try
            {
                Assert.Null(exception);
            }
            finally
            {
                service?.Dispose();
                window.Close();
            }
        });

    [Fact]
    public void SyncCoordinator_PropertyChanged_from_background_thread_does_not_crash_tray_status() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            var window = NewOffscreenWindow(-6000);
            window.Show();

            var service = new TrayIconService(window);
            var originalIsBusy = SyncCoordinator.Instance.IsBusy;

            try
            {
                Exception? backgroundException = null;

                // Dispara SyncCoordinator.PropertyChanged (→ OnSyncCoordinatorPropertyChanged →
                // UpdateStatusHeader) desde un hilo de fondo GENUINO (ni el de UI ni el dedicado de
                // UiThread) — reproduce exactamente el path que crasheaba en producción.
                var backgroundThread = new Thread(() =>
                {
                    try { SyncCoordinator.Instance.IsBusy = !originalIsBusy; }
                    catch (Exception ex) { backgroundException = ex; }
                });
                backgroundThread.Start();
                backgroundThread.Join();

                Assert.Null(backgroundException);

                // El fix marshalea la actualización real del Header vía Dispatcher.BeginInvoke: hay
                // que bombear la cola para que corra antes de verificar que se aplicó (no solo que
                // no explotó).
                UiTestApplication.PumpDispatcherUntilIdle();

                var statusMenuItem = GetPrivateField<WinForms.ToolStripMenuItem>(service, "_statusMenuItem");
                Assert.Equal(SyncCoordinator.Instance.DisplayStatus, statusMenuItem.Text);
            }
            finally
            {
                SyncCoordinator.Instance.IsBusy = originalIsBusy;
                service.Dispose();
                window.Close();
            }
        });

    [Fact]
    public void UpdateReady_invoked_from_background_thread_does_not_crash() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            var window = NewOffscreenWindow(-7000);
            window.Show();

            // Fake notifier: sin esto, OnUpdateReady dispararía un globo REAL de Windows (sonido +
            // toast) en la máquina de quien corre los tests -- efecto secundario audible. El seam
            // ITrayNotifier permite verificar el flujo sin ese ruido.
            var notifier = new RecordingTrayNotifier();
            var service = new TrayIconService(window, notifier);

            try
            {
                var onUpdateReady = typeof(TrayIconService).GetMethod(
                    "OnUpdateReady", BindingFlags.NonPublic | BindingFlags.Instance)!;

                Exception? backgroundException = null;
                var backgroundThread = new Thread(() =>
                {
                    try
                    {
                        onUpdateReady.Invoke(service, new object[] { "9.9.9" });
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is not null)
                    {
                        backgroundException = ex.InnerException;
                    }
                    catch (Exception ex)
                    {
                        backgroundException = ex;
                    }
                });
                backgroundThread.Start();
                backgroundThread.Join();

                Assert.Null(backgroundException);

                UiTestApplication.PumpDispatcherUntilIdle();

                // La notificación se pidió (una sola vez), pero contra el fake -- nunca contra el
                // NotifyIcon real, así que no hubo globo ni sonido.
                Assert.Single(notifier.Balloons);
                Assert.Equal("Actualización disponible", notifier.Balloons[0].Title);
                Assert.Contains("9.9.9", notifier.Balloons[0].Message);

                var updateMenuItem = GetPrivateField<WinForms.ToolStripMenuItem>(service, "_updateMenuItem");
                // Asserting on Available, not Visible: ToolStripItem.Visible also factors in
                // Placement, which stays None (so Visible reads false) until the ContextMenuStrip
                // actually gets laid out by being shown at least once -- this test never right-clicks
                // the tray icon to open it. Available is what "_updateMenuItem.Visible = true" in
                // TrayIconService.OnUpdateReady really sets, and it is what governs whether the item
                // shows up the next time the real menu opens, regardless of layout timing.
                Assert.True(updateMenuItem.Available);
            }
            finally
            {
                service.Dispose();
                window.Close();
            }
        });

    /// <summary>
    /// Regresión del bug real reportado el 2026-07-08: la app cerraba de verdad al tocar la [x] con
    /// el toggle "Minimizar a la bandeja al cerrar" activado. Causa: <c>IsExitRequested</c> era un
    /// latch que, puesto en true desde "Salir", nunca se reseteaba — si <c>Application.Shutdown()</c>
    /// se interrumpía a mitad de camino (p.ej. una excepción en MainViewModel.Dispose durante el
    /// cierre, atrapada por DispatcherUnhandledException y que deja la app viva), el flag quedaba
    /// pegado en true para siempre y el PRÓXIMO cierre por [x] cerraba de verdad sin importar el
    /// setting. Fix: <see cref="TrayIconService.ConsumeExitRequested"/> lee y resetea en el mismo
    /// paso, así cada intento de cierre arranca desde cero.
    /// <para/>
    /// Usa reflection sobre el setter privado de <c>IsExitRequested</c> (en vez de disparar el click
    /// real del MenuItem "Salir") a propósito: ese Click llama a <c>Application.Current.Shutdown()</c>,
    /// que tiraría abajo la Application COMPARTIDA entre todos los tests de <see cref="UiThread"/>.
    /// </summary>
    [Fact]
    public void ConsumeExitRequested_resets_flag_so_it_does_not_leak_into_the_next_close_attempt() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            var window = NewOffscreenWindow(-9000);
            window.Show();

            var service = new TrayIconService(window);

            try
            {
                // Estado inicial: nadie pidió "Salir" todavía.
                Assert.False(service.IsExitRequested);
                Assert.False(service.ConsumeExitRequested());

                // Simula que "Salir" puso el flag en true (sin pasar por el Click real, que llamaría
                // a Application.Current.Shutdown() — ver comentario de la clase).
                SetIsExitRequested(service, true);
                Assert.True(service.IsExitRequested);

                // Primer intento de cierre: lo consume y lo ve en true (comportamiento esperado de
                // un "Salir" real que sí completa el Shutdown).
                Assert.True(service.ConsumeExitRequested());

                // El intento SIGUIENTE (p.ej. un Shutdown() interrumpido que deja la app viva, y el
                // usuario después toca la [x] normal) NO debe heredar ese exit request ya consumido.
                Assert.False(service.IsExitRequested);
                Assert.False(service.ConsumeExitRequested());
            }
            finally
            {
                service.Dispose();
                window.Close();
            }
        });

    private static void SetIsExitRequested(TrayIconService service, bool value)
    {
        var property = typeof(TrayIconService).GetProperty(nameof(TrayIconService.IsExitRequested))
            ?? throw new InvalidOperationException("No se encontró la propiedad 'IsExitRequested'.");
        property.SetValue(service, value);
    }

    private static Window NewOffscreenWindow(double offset) => new()
    {
        Width = 50,
        Height = 50,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.None,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = offset,
        Top = offset,
    };

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el campo privado '{fieldName}'.");
        return (T)field.GetValue(instance)!;
    }
}
