using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioTranscriber.Core.Audio;

/// <summary>Una aplicación con audio activo en este momento: nombre visible + PID, lista para
/// mostrar en el selector de "Grabar reunión" (ver <see cref="MeetingRecorder"/>).</summary>
public sealed record AudioAppInfo(int ProcessId, string DisplayName);

/// <summary>
/// UNA sesión de audio de Windows, ya con el nombre resuelto (el <c>DisplayName</c> que reporta
/// Windows, o -- si vino vacío -- el nombre del proceso, ver <see cref="AudioAppLister.List"/>).
/// Separado de <see cref="AudioAppInfo"/> a propósito: el filtrado/dedup/orden de abajo
/// (<see cref="AudioAppLister.SelectDistinctActiveApps"/>) es lógica pura, testeable sin NAudio
/// ni <see cref="Process"/>, y no le importa CÓMO se resolvió cada nombre.
/// </summary>
public readonly record struct AudioSessionCandidate(int ProcessId, string DisplayName, bool IsSystemSession, bool IsActive);

/// <summary>
/// Lista qué aplicaciones están reproduciendo audio ahora mismo, para que "Grabar reunión" pueda
/// elegir de cuál capturar en vez de todo lo que suena en la PC (ver <see cref="MeetingRecorder"/>,
/// que recibe el PID elegido acá).
/// </summary>
public static class AudioAppLister
{
    /// <summary>
    /// Devuelve las aplicaciones con audio activo en este momento (nombre + PID), sin duplicados
    /// y ordenadas alfabéticamente. Nunca tira: sin dispositivo de salida, sin sesiones, o
    /// cualquier falla puntual al leer una sesión -- mismo criterio que
    /// <see cref="MeetingRecorder"/> con el audio del sistema, ninguna falla acá puede tirar abajo
    /// la UI -- devuelve lo que se pudo armar (en el peor caso, una lista vacía).
    /// </summary>
    public static IReadOnlyList<AudioAppInfo> List()
    {
        var candidates = new List<AudioSessionCandidate>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                TryAddCandidate(session, candidates);
            }
        }
        catch
        {
            // Sin salida de audio, driver raro, lo que sea: se devuelve lo que se pudo armar
            // (acá, nada) -- ver el comentario de la clase.
        }

        return SelectDistinctActiveApps(candidates, Environment.ProcessId);
    }

    private static void TryAddCandidate(AudioSessionControl session, List<AudioSessionCandidate> candidates)
    {
        try
        {
            int pid = unchecked((int)session.GetProcessID);
            string displayName = ResolveDisplayName(session, pid);
            bool isActive = session.State == AudioSessionState.AudioSessionStateActive;
            candidates.Add(new AudioSessionCandidate(pid, displayName, session.IsSystemSoundsSession, isActive));
        }
        catch
        {
            // La sesión murió justo mientras la leíamos (o cualquier otra falla puntual): se
            // saltea ESA sesión nada más, no se pierde el resto de la lista.
        }
    }

    /// <summary>
    /// El <c>DisplayName</c> que reporta Windows casi siempre viene vacío -- o, en aplicaciones
    /// empaquetadas, es una referencia a un recurso ("@es-ES.dll,-123") en vez de texto legible.
    /// En los dos casos cae al nombre del proceso; si el proceso ya no existe o no se puede leer,
    /// devuelve vacío (se descarta en <see cref="SelectDistinctActiveApps"/> -- nunca se muestra
    /// una entrada sin nombre en el combo).
    /// </summary>
    private static string ResolveDisplayName(AudioSessionControl session, int pid)
    {
        var raw = session.DisplayName;
        if (!string.IsNullOrWhiteSpace(raw) && !raw.StartsWith('@'))
            return raw.Trim();

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Lógica pura (sin NAudio ni Process, testeable sin dispositivos reales): de todas las
    /// sesiones encontradas se queda con las activas, saca las del sistema, la nuestra propia y
    /// las que no pudieron resolver nombre, deduplica por PID (un mismo Chrome puede tener varias
    /// sesiones) y ordena alfabéticamente para que el combo sea predecible.
    /// </summary>
    public static IReadOnlyList<AudioAppInfo> SelectDistinctActiveApps(
        IEnumerable<AudioSessionCandidate> candidates, int ownProcessId)
    {
        return candidates
            .Where(c => c.IsActive)
            .Where(c => !c.IsSystemSession)
            .Where(c => c.ProcessId > 0 && c.ProcessId != ownProcessId)
            .Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
            .GroupBy(c => c.ProcessId)
            .Select(g => g.First())
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new AudioAppInfo(c.ProcessId, c.DisplayName))
            .ToList();
    }
}
