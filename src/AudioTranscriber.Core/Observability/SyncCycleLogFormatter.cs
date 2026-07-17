using System.Text;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Observability;

/// <summary>
/// Lógica pura (sin I/O) del RESUMEN de cada ciclo de sync -- a diferencia de
/// <see cref="SyncErrorLogFormatter"/> (solo corre cuando el ciclo tira una excepción), esto se
/// pensó para loguearse SIEMPRE, con o sin error, y con o sin cambios. Escribe al MISMO archivo
/// del día (<c>logs/sync-yyyyMMdd.log</c>, ver <see cref="SyncErrorLogFormatter.ResolveLogFilePath"/>,
/// reusado tal cual acá) para no fragmentar el diagnóstico de sync en más archivos.
/// <para/>
/// Motivación (bug real 2026-07-08): el usuario reportaba "Sincronizado, 2 acciones aplicadas" sin
/// error, pero las transcripciones de la web seguían sin aparecer -- no había forma de saber, sin
/// reproducir el bug a mano, cuánto trajo el pull, cuánto se aplicó y cuánto se omitió (y por qué).
/// </summary>
public static class SyncCycleLogFormatter
{
    /// <summary>
    /// Formatea el resumen de un ciclo: conteos (pull, acciones aplicadas, pusheadas, fallos de
    /// audio) + una línea por cada entrada de <paramref name="diagnostics"/> (motivo textual de
    /// cada transcripción procesada -- ver <see cref="SyncEngine.RunAsync"/>). Termina con una
    /// línea en blanco extra para separar entradas consecutivas en el archivo append-only (mismo
    /// criterio que <see cref="CrashLogFormatter"/>/<see cref="CloseFlowLogFormatter"/>).
    /// </summary>
    public static string FormatEntry(
        DateTime timestamp,
        bool manual,
        SyncOutcome outcome,
        int pulledProjects,
        int pulledTranscriptions,
        int actionsApplied,
        int pushedCount,
        int audioDownloadFailures,
        IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var sb = new StringBuilder();
        sb.Append('[').Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ");
        sb.Append("Ciclo de sync (manual=").Append(manual).Append(", outcome=").Append(outcome).Append(")\n");
        sb.Append("Pull: ").Append(pulledProjects).Append(" proyecto(s), ")
          .Append(pulledTranscriptions).Append(" transcripción(es).\n");
        sb.Append("Aplicadas: ").Append(actionsApplied).Append(" acción(es). Pusheadas: ")
          .Append(pushedCount).Append(". Fallos de descarga de audio: ").Append(audioDownloadFailures).Append(".\n");

        foreach (var line in diagnostics)
            sb.Append("  - ").Append(line).Append('\n');

        sb.Append('\n');
        return sb.ToString();
    }
}
