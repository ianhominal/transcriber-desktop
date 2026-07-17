using AudioTranscriber.Core.Runtime;
using Microsoft.Win32;

namespace AudioTranscriber.App;

/// <summary>
/// Inicio automático con Windows vía el registro (HKCU\...\Run). El registro es la fuente
/// de verdad; <see cref="AppSettings.AutoStartEnabled"/> es solo un espejo para que la UI no
/// tenga que releerlo cada vez (ver comentario ahí). Los nombres de clave/valor y el armado del
/// valor a escribir viven en <see cref="AutoStartRegistration"/> (Core, testeable sin registro
/// real); acá solo se hace la I/O real contra <see cref="Registry"/>.
/// </summary>
public static class AutoStartHelper
{
    /// <summary>True si existe una entrada de inicio automático para esta app en el registro.</summary>
    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistration.RunKeyPath, writable: false);
            return key?.GetValue(AutoStartRegistration.ValueName) is string;
        }
        catch
        {
            // Si no se puede leer el registro (permisos, etc.), asumimos que no está configurado.
            return false;
        }
    }

    /// <summary>Agrega o quita la entrada de inicio automático en HKCU\...\Run.</summary>
    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistration.RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(AutoStartRegistration.RunKeyPath);

            if (enabled)
            {
                // Environment.ProcessPath es preferido sobre Process.GetCurrentProcess().MainModule.FileName
                // en .NET moderno (más liviano, no requiere abrir el proceso completo).
                var value = AutoStartRegistration.BuildRegistryValue(Environment.ProcessPath);
                if (value is null)
                    return;

                key.SetValue(AutoStartRegistration.ValueName, value);
            }
            else
            {
                key.DeleteValue(AutoStartRegistration.ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Si falla la escritura en el registro (permisos, etc.), no es fatal: solo no
            // queda persistido el inicio automático.
        }
    }
}
