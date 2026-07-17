using System;

namespace AudioTranscriber.Core.Runtime;

/// <summary>
/// Lógica pura de resolución de tema, testeable sin WPF ni el registro real de Windows: mapea el
/// string persistido en <c>AppSettings.Theme</c> al enum <see cref="AppTheme"/>, y resuelve
/// "Sistema" al tema efectivo (Light o Dark) según si Windows está en modo oscuro. La capa que
/// hace la I/O real (leer el registro y tocar <c>Application.Resources</c>) es
/// <c>AudioTranscriber.App.ThemeManager</c>; esta clase existe para poder testear la resolución en
/// sí (mismo patrón que <see cref="AutoStartRegistration"/> / <c>AutoStartHelper</c>).
/// </summary>
public static class ThemeResolver
{
    /// <summary>
    /// Parsea el string persistido en AppSettings.Theme ("Light"/"Dark"/"System"). Cualquier valor
    /// desconocido, vacío o null cae a System (default conservador: sigue el tema de Windows).
    /// </summary>
    public static AppTheme Parse(string? value) =>
        Enum.TryParse<AppTheme>(value, ignoreCase: true, out var parsed) ? parsed : AppTheme.System;

    /// <summary>
    /// Resuelve el tema efectivo a aplicar: siempre Light o Dark, nunca System (System se resuelve
    /// acá mismo contra <paramref name="systemIsDark"/>, que el caller obtiene del registro de
    /// Windows).
    /// </summary>
    public static AppTheme ResolveEffective(AppTheme theme, bool systemIsDark) => theme switch
    {
        AppTheme.Dark => AppTheme.Dark,
        AppTheme.Light => AppTheme.Light,
        AppTheme.System => systemIsDark ? AppTheme.Dark : AppTheme.Light,
        _ => AppTheme.Light,
    };
}
