namespace AudioTranscriber.Core.Updates;

/// <summary>Resultado discriminado de un chequeo de actualización (manual o automático).</summary>
public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    Error,
}

/// <summary>
/// Resultado puro de un chequeo de actualización: qué encontró y con qué datos, sin ninguna
/// dependencia de Velopack ni de WPF. La I/O real (Velopack + red) vive en
/// <c>AudioTranscriber.App.UpdateService.CheckForUpdateManualAsync</c>, que arma este resultado;
/// esta clase solo modela el resultado para que se pueda testear el mapeo a UI sin tocar disco/red.
/// </summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string? Version, string? ErrorMessage)
{
    /// <summary>Ya está en la última versión disponible.</summary>
    public static UpdateCheckResult UpToDate(string currentVersion) =>
        new(UpdateCheckStatus.UpToDate, currentVersion, null);

    /// <summary>Hay una versión nueva y ya se descargó (lista para <c>ApplyAndRestart</c>).</summary>
    public static UpdateCheckResult Available(string newVersion) =>
        new(UpdateCheckStatus.Available, newVersion, null);

    /// <summary>No se pudo completar el chequeo (sin conexión, GitHub caído, no instalado, etc).</summary>
    public static UpdateCheckResult Error(string message) =>
        new(UpdateCheckStatus.Error, null, message);
}
