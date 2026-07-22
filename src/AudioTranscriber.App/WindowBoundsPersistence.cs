using System.Windows;
using AudioTranscriber.Core.Ui;
using WinForms = System.Windows.Forms;

namespace AudioTranscriber.App;

/// <summary>
/// Plomería WPF de "recordar tamaño/posición/estado de MainWindow": lee/escribe
/// <see cref="AppSettings"/> y las propiedades reales de la <see cref="Window"/>, pero delega toda
/// la matemática (¿los bounds guardados siguen visibles? ¿qué tamaño inicial usar en el primer
/// arranque?) a la lógica pura y testeada de <see cref="AudioTranscriber.Core.Ui.WindowBoundsValidator"/>
/// y <see cref="AudioTranscriber.Core.Ui.InitialWindowSizer"/> (Core, sin WPF/WinForms).
/// </summary>
internal static class WindowBoundsPersistence
{
    private const string MaximizedState = "Maximized";
    private const string NormalState = "Normal";

    /// <summary>
    /// Aplica los bounds guardados (o, en el primer arranque / si los guardados quedaron fuera de
    /// pantalla, un tamaño inicial calculado por <see cref="InitialWindowSizer"/>) a
    /// <paramref name="window"/>. DEBE llamarse ANTES de Show() -- WPF solo centra automáticamente
    /// con WindowStartupLocation=CenterScreen (el default); acá se fuerza a Manual para que
    /// Left/Top/Width/Height/WindowState calculados acá no se pisen.
    /// </summary>
    public static void Apply(Window window)
    {
        var settings = AppSettings.Instance;
        var screens = GetScreenWorkAreas();
        var primaryWorkArea = GetPrimaryWorkArea();
        var fallback = InitialWindowSizer.Compute(primaryWorkArea, window.MinWidth, window.MinHeight);

        var hasSavedBounds = settings.WindowWidth is not null && settings.WindowHeight is not null &&
                              settings.WindowLeft is not null && settings.WindowTop is not null;

        ScreenRect targetBounds;
        if (hasSavedBounds)
        {
            var saved = new ScreenRect(
                settings.WindowLeft!.Value, settings.WindowTop!.Value,
                settings.WindowWidth!.Value, settings.WindowHeight!.Value);
            targetBounds = WindowBoundsValidator.Validate(saved, screens, fallback);
        }
        else
        {
            targetBounds = fallback;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = targetBounds.X;
        window.Top = targetBounds.Y;
        // Math.Max: piso de seguridad si MinWidth/MinHeight subieron en una versión posterior a
        // cuando se guardó este settings.json (bounds guardados más chicos que el mínimo actual).
        window.Width = Math.Max(targetBounds.Width, window.MinWidth);
        window.Height = Math.Max(targetBounds.Height, window.MinHeight);
        window.WindowState = hasSavedBounds && settings.WindowState == MaximizedState
            ? WindowState.Maximized
            : WindowState.Normal;
    }

    /// <summary>
    /// Guarda el tamaño/posición/estado ACTUAL de <paramref name="window"/> en
    /// <see cref="AppSettings"/>. Debe llamarse SOLO en un cierre real de la app (ver
    /// App.xaml.cs.OnMainWindowClosing, rama <c>WindowCloseAction.Exit</c>) -- llamarlo cada vez que
    /// la ventana se oculta a la bandeja pisaría un layout maximizado/dimensionado a mano con lo que
    /// sea que tuviera la ventana en ese momento transitorio.
    /// </summary>
    public static void Save(Window window)
    {
        var settings = AppSettings.Instance;

        if (window.WindowState == WindowState.Normal)
        {
            settings.WindowState = NormalState;
            settings.WindowLeft = window.Left;
            settings.WindowTop = window.Top;
            settings.WindowWidth = window.Width;
            settings.WindowHeight = window.Height;
        }
        else
        {
            // Maximized: RestoreBounds es el tamaño "ventana" al que volvería, NO el tamaño de
            // pantalla completa -- guardarlo así hace que desmaximizar en el próximo arranque quede
            // bien. Minimized (caso raro: cerrar de verdad -- "Salir" de la bandeja -- mientras la
            // ventana estaba minimizada) usa el mismo RestoreBounds pero se guarda como "Normal":
            // Minimized no es un estado válido para reabrir la ventana (eso lo decide
            // StartupWindowMode/el flujo de bandeja, no este setting).
            var bounds = window.RestoreBounds;
            settings.WindowState = window.WindowState == WindowState.Maximized ? MaximizedState : NormalState;
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
        }

        settings.Save();
    }

    private static IReadOnlyList<ScreenRect> GetScreenWorkAreas()
    {
        var screens = WinForms.Screen.AllScreens;
        var result = new ScreenRect[screens.Length];
        for (var i = 0; i < screens.Length; i++)
            result[i] = ToScreenRect(screens[i].WorkingArea);
        return result;
    }

    private static ScreenRect GetPrimaryWorkArea()
    {
        var primary = WinForms.Screen.PrimaryScreen;
        if (primary is not null)
            return ToScreenRect(primary.WorkingArea);

        // Defensivo: en la práctica siempre hay al menos un monitor en una PC con sesión gráfica,
        // pero si PrimaryScreen viniera null y AllScreens vacío, no hay forma de calcular nada real
        // -- se cae a un tamaño chico razonable en vez de tirar.
        var all = WinForms.Screen.AllScreens;
        return all.Length > 0 ? ToScreenRect(all[0].WorkingArea) : new ScreenRect(0, 0, 1024, 768);
    }

    private static ScreenRect ToScreenRect(System.Drawing.Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
