using Microsoft.Win32;

namespace AudioTranscriber.App;

/// <summary>
/// Lee, del registro de Windows, qué aplicaciones están usando el micrófono AHORA MISMO -- la señal
/// que <see cref="MeetingDetectionService"/> le pasa a
/// <see cref="AudioTranscriber.Core.Meetings.MeetingDetector"/> (Core, puro) para decidir si hay una
/// reunión en curso (Meet, Zoom, Teams, Discord voz: las cuatro capturan el mic al entrar a la
/// llamada).
/// <para/>
/// Windows trackea esto en
/// <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone</c>
/// -- el mismo mecanismo detrás del indicador de privacidad del micrófono de la barra de tareas.
/// Cada app tiene una subclave con <c>LastUsedTimeStart</c>/<c>LastUsedTimeStop</c> (FILETIME,
/// REG_QWORD): si <c>LastUsedTimeStop == 0</c> (o <c>Start &gt; Stop</c>), esa app está usando el
/// mic EN ESTE MOMENTO. Dos formas de subclave:
/// <list type="bullet">
/// <item>Apps empaquetadas (UWP/MSIX): subclave directa bajo <c>microphone</c>, nombrada con el
/// PackageFamilyName.</item>
/// <item>Apps win32 (navegadores, Zoom, Discord...): bajo <c>microphone\NonPackaged</c>, con la
/// ruta completa del .exe como nombre de subclave, con cada <c>\</c> reemplazado por <c>#</c>.</item>
/// </list>
/// Nunca tira: sin este store (versión vieja de Windows), sin permisos, o cualquier falla puntual
/// leyendo una subclave -- se devuelve lo que se pudo armar (en el peor caso, una lista vacía),
/// mismo criterio que <see cref="AudioTranscriber.Core.Audio.AudioAppLister"/> con las sesiones de
/// audio.
/// </summary>
public static class MicrophoneUsageReader
{
    private const string ConsentStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private const string NonPackagedSubKeyName = "NonPackaged";

    /// <summary>
    /// Identificadores (ruta completa del .exe para apps win32, PackageFamilyName para
    /// empaquetadas) de las apps que están usando el micrófono ahora mismo.
    /// </summary>
    public static IReadOnlyList<string> GetAppsUsingMicrophoneNow()
    {
        var result = new List<string>();
        try
        {
            using var micKey = Registry.CurrentUser.OpenSubKey(ConsentStorePath, writable: false);
            if (micKey is null)
                return result;

            // Apps empaquetadas: subclaves directas bajo "microphone" (salvo "NonPackaged", que es
            // un contenedor aparte, no una app en sí).
            foreach (var subKeyName in micKey.GetSubKeyNames())
            {
                if (string.Equals(subKeyName, NonPackagedSubKeyName, StringComparison.OrdinalIgnoreCase))
                    continue;
                TryAddIfInUseNow(micKey, subKeyName, subKeyName, result);
            }

            // Apps win32: bajo "NonPackaged", ruta completa con '#' en vez de '\'.
            using var nonPackagedKey = micKey.OpenSubKey(NonPackagedSubKeyName, writable: false);
            if (nonPackagedKey is not null)
            {
                foreach (var subKeyName in nonPackagedKey.GetSubKeyNames())
                {
                    var exePath = subKeyName.Replace('#', '\\');
                    TryAddIfInUseNow(nonPackagedKey, subKeyName, exePath, result);
                }
            }
        }
        catch
        {
            // Registro no legible (permisos, store inexistente en esta versión de Windows, etc.):
            // se devuelve lo que se pudo armar -- ver el comentario de la clase.
        }

        return result;
    }

    /// <summary>
    /// Si la subclave <paramref name="subKeyName"/> de <paramref name="parent"/> tiene
    /// LastUsedTimeStop == 0 (o Start &gt; Stop) -- "en uso ahora" -- agrega
    /// <paramref name="identifier"/> a <paramref name="result"/>. Envuelto en su propio try/catch:
    /// una subclave puntual rara/corrupta no debe perder el resto del listado.
    /// </summary>
    private static void TryAddIfInUseNow(RegistryKey parent, string subKeyName, string identifier, List<string> result)
    {
        try
        {
            using var key = parent.OpenSubKey(subKeyName, writable: false);
            if (key is null)
                return;

            var startValue = key.GetValue("LastUsedTimeStart");
            if (startValue is null)
                return; // nunca se usó.

            var stopValue = key.GetValue("LastUsedTimeStop");
            long start = Convert.ToInt64(startValue);
            long stop = stopValue is null ? 0 : Convert.ToInt64(stopValue);

            if (stop == 0 || start > stop)
                result.Add(identifier);
        }
        catch
        {
            // Ver el comentario de GetAppsUsingMicrophoneNow: se saltea SOLO esta subclave.
        }
    }
}
