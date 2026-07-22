namespace AudioTranscriber.Core.Ui;

/// <summary>
/// Rectángulo puro (sin dependencia de WPF ni WinForms) usado para representar tanto los bounds
/// guardados/deseados de una ventana como el área de trabajo (work area, sin la taskbar) de un
/// monitor. Existe para que <see cref="WindowBoundsValidator"/> y <see cref="InitialWindowSizer"/>
/// puedan vivir en Core (net8.0 plano) sin referenciar System.Windows.Rect (WPF,
/// AudioTranscriber.App) ni System.Drawing.Rectangle (WinForms, ints en vez de doubles).
/// </summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}
