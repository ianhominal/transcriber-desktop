namespace AudioTranscriber.Core.Ui;

/// <summary>
/// Valida que unos bounds de ventana guardados (posición + tamaño) todavía queden visibles en
/// alguno de los monitores conectados HOY -- robustez multi-monitor: si el usuario guardó la
/// ventana en un segundo monitor que después desconectó, o los bounds quedaron con coordenadas
/// raras (negativas, corruptas), no hay que arrancar la ventana fuera de vista, sin forma de
/// agarrarla con el mouse.
/// <para/>
/// Lógica pura y testeable, separada de la capa WPF (que solo junta los <see cref="ScreenRect"/>
/// reales vía System.Windows.Forms.Screen.AllScreens y aplica el resultado a la Window) -- mismo
/// criterio que <see cref="AudioTranscriber.Core.Runtime.WindowCloseBehavior"/>.
/// </summary>
public static class WindowBoundsValidator
{
    /// <summary>
    /// Ancho/alto mínimo (en DIPs) que la intersección entre los bounds guardados y el work area de
    /// un monitor tiene que tener para considerar esos bounds "suficientemente visibles" y
    /// reusarlos tal cual. Pensado para garantizar que se pueda VER y AGARRAR la ventana (la barra
    /// de título tiene que asomar lo suficiente), no solo que un par de píxeles technically
    /// intersequen.
    /// </summary>
    public const double MinVisibleWidth = 120;

    public const double MinVisibleHeight = 40;

    /// <summary>
    /// Devuelve <paramref name="savedBounds"/> tal cual si queda suficientemente visible en alguno
    /// de los <paramref name="screens"/> (sin clampear: dejar que un sliver cuelgue fuera de
    /// pantalla es el comportamiento normal de Windows); si no, cae a <paramref name="fallback"/>
    /// (calculado por el llamador, típicamente con <see cref="InitialWindowSizer"/> centrado en el
    /// monitor primario).
    /// </summary>
    public static ScreenRect Validate(ScreenRect savedBounds, IReadOnlyList<ScreenRect> screens, ScreenRect fallback)
    {
        foreach (var screen in screens)
        {
            var intersection = Intersect(savedBounds, screen);
            if (intersection.Width >= MinVisibleWidth && intersection.Height >= MinVisibleHeight)
                return savedBounds;
        }

        return fallback;
    }

    private static ScreenRect Intersect(ScreenRect a, ScreenRect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);

        var width = Math.Max(0, x2 - x1);
        var height = Math.Max(0, y2 - y1);
        return new ScreenRect(x1, y1, width, height);
    }
}
