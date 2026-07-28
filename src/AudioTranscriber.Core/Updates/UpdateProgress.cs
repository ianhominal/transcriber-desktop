namespace AudioTranscriber.Core.Updates;

/// <summary>
/// Progreso de la DESCARGA de una actualización ya encontrada (distinto del chequeo en sí, que
/// tarda un segundo). Lo emite <c>AudioTranscriber.App.UpdateService.DownloadProgressChanged</c> a
/// partir del callback de Velopack (<c>UpdateManager.DownloadUpdatesAsync</c>), que reporta solo el
/// porcentaje -- el tamaño total sale aparte, del asset del <c>UpdateInfo</c> (ver
/// <see cref="TotalBytes"/>), no del callback.
/// </summary>
/// <param name="Percent">Porcentaje descargado, 0-100.</param>
/// <param name="TotalBytes">
/// Tamaño del paquete completo en bytes, si Velopack lo informó (<c>VelopackAsset.Size</c>). Puede
/// venir <c>null</c> o cero -- en ese caso no se muestra ningún tamaño (nunca se inventa un número,
/// ver <see cref="UpdateUiTextFormatter.FormatDownloadingText"/>).
/// </param>
public sealed record UpdateProgress(int Percent, long? TotalBytes);
