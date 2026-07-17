using System;
using System.Windows;
using AudioTranscriber.Core.Runtime;
using Microsoft.Win32;

namespace AudioTranscriber.App;

/// <summary>
/// Aplica el tema claro/oscuro reemplazando en runtime el ResourceDictionary de colores
/// (Styles/Colors.Light.xaml o Styles/Colors.Dark.xaml) dentro de
/// Application.Current.Resources.MergedDictionaries. Todos los consumidores de esas claves de
/// color usan DynamicResource (no StaticResource, ver comentario en App.xaml) para que el cambio
/// se refleje en vivo, sin reiniciar la ventana.
/// <para/>
/// La resolución de qué tema corresponde (Claro/Oscuro fijos, o el de Windows si el setting es
/// "Sistema") es lógica pura en <see cref="ThemeResolver"/>; esta clase solo hace la I/O real:
/// leer el registro de Windows y tocar Application.Resources.
/// </summary>
public static class ThemeManager
{
    // "/AssemblyName;component/ruta" (component URI), NO un string relativo simple. Un
    // ResourceDictionary.Source seteado a mano en código (a diferencia de Source="..." dentro de
    // XAML, donde el compilador de markup lo reescribe solo a esta misma forma) necesita el
    // component URI completo: un Uri relativo "pelado" como "Styles/Colors.Dark.xaml" NO resuelve
    // contra los recursos compilados del assembly y tira
    // "System.IO.IOException: No se encuentra el recurso 'styles/colors.dark.xaml'." recién en
    // runtime (no lo agarra dotnet build ni un test que no llegue a ejecutar Apply). AssemblyName
    // tiene que matchear <AssemblyName> del .csproj ("AudioTranscriber", no "AudioTranscriber.App").
    private const string LightSource = "/AudioTranscriber;component/Styles/Colors.Light.xaml";
    private const string DarkSource = "/AudioTranscriber;component/Styles/Colors.Dark.xaml";

    // HKCU\...\Personalize\AppsUseLightTheme: 1 = Windows en modo claro, 0 = modo oscuro. Es el
    // mismo valor que usan Explorer y el resto del shell para decidir su propio tema.
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string PersonalizeValueName = "AppsUseLightTheme";

    private static bool _listeningToSystemChanges;

    /// <summary>Setting elegido por el usuario (Light/Dark/System). System por default.</summary>
    public static AppTheme CurrentSetting { get; private set; } = AppTheme.System;

    /// <summary>Tema efectivamente aplicado ahora mismo (nunca System: ya resuelto a Light o Dark).</summary>
    public static AppTheme EffectiveTheme { get; private set; } = AppTheme.Light;

    /// <summary>
    /// Aplica <paramref name="theme"/>: resuelve el tema efectivo (leyendo el registro si hace
    /// falta) y reemplaza el ResourceDictionary de colores activo. Se llama al arrancar (con el
    /// valor guardado en AppSettings.Theme) y cada vez que el usuario cambia el selector en
    /// Configuración.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        CurrentSetting = theme;
        EffectiveTheme = ThemeResolver.ResolveEffective(theme, IsSystemDarkTheme());

        var app = Application.Current;
        if (app is null)
            return; // defensivo: no hay Application viva (p.ej. algún test unitario aislado).

        var source = EffectiveTheme == AppTheme.Dark ? DarkSource : LightSource;
        var newDictionary = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        var dictionaries = app.Resources.MergedDictionaries;
        var existingIndex = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var src = dictionaries[i].Source?.OriginalString;
            if (src is not null && (src.EndsWith("Colors.Light.xaml", StringComparison.OrdinalIgnoreCase)
                                     || src.EndsWith("Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
            dictionaries[existingIndex] = newDictionary;
        else
            dictionaries.Insert(0, newDictionary);

        EnsureListeningToSystemChanges();
    }

    /// <summary>
    /// True si Windows está en modo oscuro (AppsUseLightTheme = 0). Best-effort: si no se puede
    /// leer el registro (permisos, clave inexistente en Windows viejo, etc.) asumimos modo claro
    /// (comportamiento actual de la app, sin cambios para quien no tiene esa clave).
    /// </summary>
    public static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            return key?.GetValue(PersonalizeValueName) is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Mientras el setting sea "Sistema", re-resuelve el tema cuando Windows notifica un cambio de
    /// preferencias (incluye el toggle claro/oscuro de Configuración de Windows), así el usuario no
    /// necesita reiniciar la app para verlo. Una sola suscripción para toda la vida del proceso
    /// (idempotente vía _listeningToSystemChanges); no hace falta desuscribir explícitamente
    /// porque vive tanto como el proceso.
    /// </summary>
    private static void EnsureListeningToSystemChanges()
    {
        if (_listeningToSystemChanges)
            return;

        _listeningToSystemChanges = true;
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
    }

    private static void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (CurrentSetting == AppTheme.System)
            Apply(AppTheme.System);
    }
}
