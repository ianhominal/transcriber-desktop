using System.Text.RegularExpressions;

namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Resultado de detectar que el backend rechazó un borrado en cascada de un proyecto porque tiene
/// subproyectos y/o transcripciones (guard de confirmación del bug de pérdida de datos C1, ver
/// contrato en <c>api/sync/push/route.ts</c>). El desktop todavía no gestiona jerarquía, así que
/// nunca manda <c>projects.cascadeDeletes</c>: cualquier proyecto con descendientes que intente
/// borrar termina acá, reportado en <c>errors[]</c> en vez de borrado.
/// </summary>
public sealed record CascadeDeleteRejection(string ProjectId, int? ChildProjectCount, int? TranscriptionCount);

/// <summary>
/// Lógica pura para interpretar <c>errors[]</c> de la respuesta de POST /api/sync/push: strings
/// planos, sin ningún código/tipo estructurado (ver comentario de cabecera de
/// <c>api/sync/push/route.ts</c>), así que la única forma de distinguir el rechazo de un borrado
/// en cascada de cualquier otro error del batch es matchear la forma exacta del mensaje. Sin I/O,
/// testeable sin mocks (mismo criterio que <see cref="SyncSessionGuard"/>/<see cref="TokenExpiryPolicy"/>).
/// </summary>
public static class PushErrorHandling
{
    // Forma EXACTA armada por el backend (api/sync/push/route.ts):
    // `El proyecto ${id} tiene ${childProjectCount} subproyecto(s) y ${transcriptionCount} transcripción(es); confirmá el borrado desde la web.`
    // Anclado (^...$) a propósito: cada entrada de errors[] es un mensaje independiente, no hace
    // falta (ni conviene) matchear un substring que podría aparecer parcialmente en otro error.
    private static readonly Regex CascadeDeleteRejectionPattern = new(
        @"^El proyecto (?<id>\S+) tiene (?<children>\d+) subproyecto\(s\) y (?<transcriptions>\d+) transcripción\(es\); confirmá el borrado desde la web\.$",
        RegexOptions.Compiled);

    /// <summary>
    /// Intenta interpretar <paramref name="errorMessage"/> como el rechazo de un borrado en
    /// cascada. Null si el mensaje no tiene esa forma exacta -- cualquier otro error de
    /// <c>errors[]</c> (proyecto inválido, error SQL, error de transcripción, etc.) no matchea.
    /// </summary>
    public static CascadeDeleteRejection? TryParseCascadeDeleteRejection(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
            return null;

        var match = CascadeDeleteRejectionPattern.Match(errorMessage);
        if (!match.Success)
            return null;

        var childCount = int.TryParse(match.Groups["children"].Value, out var c) ? c : (int?)null;
        var transcriptionCount = int.TryParse(match.Groups["transcriptions"].Value, out var t) ? t : (int?)null;
        return new CascadeDeleteRejection(match.Groups["id"].Value, childCount, transcriptionCount);
    }

    /// <summary>
    /// true si, ante este error, el ítem pendiente NO debe reintentarse en el próximo ciclo --
    /// queda resuelto como "rechazado, necesita acción manual" en vez de dirty/pendiente para
    /// siempre. Hoy el único caso es el rechazo de borrado en cascada: reintentar el mismo push va
    /// a fallar exactamente igual hasta que el usuario confirme desde la web, así que insistir solo
    /// generaría un loop de reintentos inútil. Cualquier otro error (transitorio, red, bug del
    /// servidor) mantiene el comportamiento de reintento normal que ya tiene el resto del sync --
    /// esta función no lo toca.
    /// </summary>
    public static bool ShouldSkipRetry(CascadeDeleteRejection? rejection) => rejection is not null;
}
