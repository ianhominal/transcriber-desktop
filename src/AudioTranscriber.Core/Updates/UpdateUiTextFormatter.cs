namespace AudioTranscriber.Core.Updates;

/// <summary>
/// Formatea los textos que ve el usuario en el flujo de "Buscar actualizaciones" (SettingsWindow)
/// y en el banner de MainWindow, a partir de <see cref="UpdateCheckResult"/> o de la versión cruda
/// del assembly. Lógica pura, sin dependencias de Velopack ni de WPF — separada a propósito para
/// poder testear el mapeo resultado→texto sin correr ningún chequeo de red real.
/// </summary>
public static class UpdateUiTextFormatter
{
    /// <summary>Texto mientras el chequeo manual está en curso (botón deshabilitado).</summary>
    public const string CheckingText = "Buscando actualizaciones…";

    private const string GenericErrorText = "No se pudo verificar (revisá tu conexión).";

    /// <summary>
    /// "Ejecutando vX.Y.Z" para el encabezado de SettingsWindow. Etiquetado a propósito como
    /// "Ejecutando" (no solo "Versión"): el <paramref name="version"/> que recibe viene de
    /// <c>Assembly.GetExecutingAssembly().GetName().Version</c> (la versión REAL en memoria del
    /// proceso), a diferencia de <see cref="FormatResult"/> más abajo, que muestra la versión que
    /// Velopack reporta desde disco (<c>UpdateManager.CurrentVersion</c>) tras un chequeo manual —
    /// pueden diferir si una actualización quedó a medio aplicar, y el usuario necesita poder
    /// distinguir cuál es cuál para diagnosticar ese caso.
    /// </summary>
    public static string FormatCurrentVersion(string version) => $"Ejecutando v{version}";

    /// <summary>
    /// Texto de resultado tras un chequeo manual. Nota: <see cref="UpdateCheckStatus.Available"/>
    /// ya implica descargada (ver <see cref="UpdateCheckResult"/>), así que el texto no habla de
    /// "descargando" — el chequeo y la descarga son un único paso awaited en
    /// <c>UpdateService.CheckForUpdateManualAsync</c>.
    /// </summary>
    public static string FormatResult(UpdateCheckResult result) => result.Status switch
    {
        UpdateCheckStatus.UpToDate => $"Estás en la última versión (v{result.Version}).",
        UpdateCheckStatus.Available => $"Hay una versión nueva ({result.Version}). Ya está descargada y lista para instalar.",
        UpdateCheckStatus.Error => string.IsNullOrWhiteSpace(result.ErrorMessage) ? GenericErrorText : result.ErrorMessage,
        _ => string.Empty,
    };

    /// <summary>Texto del banner de MainWindow cuando hay una actualización lista para instalar.</summary>
    public static string FormatBannerText(string newVersion) => $"Hay una actualización disponible ({newVersion}).";

    /// <summary>True solo cuando el resultado dice que ya hay una versión nueva descargada.</summary>
    public static bool ShouldShowRestartButton(UpdateCheckResult? result) =>
        result?.Status == UpdateCheckStatus.Available;

    /// <summary>
    /// Texto del estado PASIVO del updater en SettingsWindow (sección "Actualizaciones"): a
    /// diferencia de <see cref="FormatResult"/> (resultado de un chequeo manual recién terminado),
    /// esto formatea el ÚLTIMO resultado conocido (<c>UpdateService.LastResult</c>), sin disparar
    /// ningún chequeo — se llama al abrir la ventana y cada vez que un chequeo automático (al abrir
    /// la app o el periódico en background) termina mientras la ventana sigue abierta. Null
    /// significa que todavía no terminó ningún chequeo en esta sesión (p.ej. Settings se abrió antes
    /// de que el chequeo automático de arranque completara) — se reusa <see cref="CheckingText"/>
    /// porque, en la práctica, siempre hay uno en curso o a punto de arrancar.
    /// </summary>
    public static string FormatPassiveStatus(UpdateCheckResult? lastResult) => lastResult switch
    {
        null => CheckingText,
        { Status: UpdateCheckStatus.UpToDate } r => $"Al día (v{r.Version}).",
        { Status: UpdateCheckStatus.Available } r => $"Hay una actualización disponible: {r.Version}.",
        { Status: UpdateCheckStatus.Error } r =>
            string.IsNullOrWhiteSpace(r.ErrorMessage) ? GenericErrorText : r.ErrorMessage,
        _ => string.Empty,
    };
}
