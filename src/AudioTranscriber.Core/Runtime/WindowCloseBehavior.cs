namespace AudioTranscriber.Core.Runtime;

/// <summary>Acción real que debe tomar el handler de Closing de MainWindow.</summary>
public enum WindowCloseAction
{
    MinimizeToTray,
    Exit,
}

/// <summary>
/// Mapea el setting "Minimizar a la bandeja al cerrar" (más la salida explícita desde el menú de
/// la bandeja) a la acción que corresponde tomar al cerrar la ventana principal. Lógica pura y
/// testeable, separada de App.xaml.cs (que solo aplica la acción resultante con
/// <c>e.Cancel</c>/<c>Hide</c>/dejar cerrar).
/// </summary>
public static class WindowCloseBehavior
{
    /// <summary>
    /// <paramref name="exitRequested"/> es true cuando el cierre vino del "Salir" explícito del
    /// menú de la bandeja: ese SIEMPRE cierra la app de verdad, sin importar el setting.
    /// </summary>
    public static WindowCloseAction Resolve(bool minimizeToTrayOnClose, bool exitRequested)
    {
        if (exitRequested)
            return WindowCloseAction.Exit;

        return minimizeToTrayOnClose ? WindowCloseAction.MinimizeToTray : WindowCloseAction.Exit;
    }
}
