namespace AudioTranscriber.Core.Runtime;

/// <summary>
/// Tema visual elegido por el usuario en Configuración. Se persiste como string (ver
/// <c>AudioTranscriber.App.AppSettings.Theme</c>, mismo criterio que otros settings tipo string
/// de la app como <c>Engine</c>) y se resuelve a un tema efectivo (Light o Dark) con
/// <see cref="ThemeResolver.ResolveEffective"/>.
/// </summary>
public enum AppTheme
{
    Light,
    Dark,
    System,
}
