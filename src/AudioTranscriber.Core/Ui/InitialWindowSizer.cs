namespace AudioTranscriber.Core.Ui;

/// <summary>
/// Calcula el tamaño/posición inicial de la ventana principal cuando todavía no hay ningún estado
/// guardado (primer arranque) o cuando <see cref="WindowBoundsValidator"/> descartó los bounds
/// guardados por quedar fuera de pantalla. En vez de un tamaño fijo (el 1060x760 que tenía
/// MainWindow.xaml antes), dimensiona en proporción al monitor: entra bien tanto en una laptop
/// chica como en un 4K, sin quedar ridículamente chica ni ridículamente grande.
/// <para/>
/// Lógica pura y testeable -- la capa WPF solo junta el work area real (vía
/// System.Windows.Forms.Screen.PrimaryScreen.WorkingArea) y aplica el resultado.
/// </summary>
public static class InitialWindowSizer
{
    /// <summary>Fracción del work area que ocupa la ventana por default (antes de los clamps de min/max).</summary>
    public const double SizeFraction = 0.8;

    /// <summary>
    /// Calcula un rect centrado en <paramref name="workArea"/>, del <see cref="SizeFraction"/> de su
    /// tamaño, nunca más chico que <paramref name="minWidth"/>/<paramref name="minHeight"/> (los
    /// MinWidth/MinHeight reales de la Window) ni más grande que <paramref name="maxWidth"/>/
    /// <paramref name="maxHeight"/> (tope razonable para no volverse absurdamente grande en un
    /// monitor 4K/ultra-wide).
    /// </summary>
    public static ScreenRect Compute(
        ScreenRect workArea,
        double minWidth,
        double minHeight,
        double maxWidth = 1600,
        double maxHeight = 1000)
    {
        var width = Math.Max(minWidth, Math.Min(workArea.Width * SizeFraction, maxWidth));
        var height = Math.Max(minHeight, Math.Min(workArea.Height * SizeFraction, maxHeight));

        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + (workArea.Height - height) / 2;

        return new ScreenRect(x, y, width, height);
    }
}
