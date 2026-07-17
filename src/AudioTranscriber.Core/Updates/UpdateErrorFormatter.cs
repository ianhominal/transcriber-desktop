using System;
using System.Net;
using System.Net.Http;

namespace AudioTranscriber.Core.Updates;

/// <summary>
/// Traduce una falla del chequeo de actualizaciones a algo que el usuario pueda entender y accionar.
///
/// Bugfix 2026-07-15: TODA falla decía "No se pudo verificar (revisá tu conexión)". Un usuario real
/// la vio durante horas con la conexión perfecta: lo que pasaba era un **429 de GitHub** (límite de
/// 60 consultas por hora por IP sin token), disparado por un timer roto que chequeaba miles de
/// veces por segundo. El mensaje lo mandó a revisar su router mientras el problema estaba acá
/// adentro. Un mensaje de error que apunta al lugar equivocado es peor que no tener mensaje.
/// </summary>
public static class UpdateErrorFormatter
{
    /// <summary>Mensaje para el usuario según la excepción que tiró el chequeo.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is HttpRequestException http)
        {
            // 429: GitHub cortó por exceso de consultas. NO es la conexión del usuario, y esperar
            // es literalmente la única acción que sirve.
            if (http.StatusCode == HttpStatusCode.TooManyRequests || MentionsTooManyRequests(http.Message))
                return "Demasiadas consultas seguidas a GitHub. Esperá un rato y probá de nuevo.";

            if (http.StatusCode == HttpStatusCode.NotFound)
                return "No se encontró la información de actualizaciones. Probá más tarde.";

            if (http.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                return "GitHub está con problemas en este momento. Probá más tarde.";

            // Sin StatusCode = nunca hubo respuesta: DNS, sin red, TLS. Acá sí es la conexión.
            if (http.StatusCode is null)
                return "No se pudo conectar. Revisá tu conexión a internet.";

            return "No se pudo verificar si hay una versión nueva. Probá más tarde.";
        }

        if (ex is TaskCanceledException or TimeoutException)
            return "La verificación tardó demasiado. Probá de nuevo en un momento.";

        return "No se pudo verificar si hay una versión nueva. Probá más tarde.";
    }

    /// <summary>
    /// Algunas capas envuelven el 429 y pierden el <c>StatusCode</c>, dejando solo el texto
    /// ("Response status code does not indicate success: 429 (too many requests)") — que fue
    /// EXACTAMENTE la forma en que llegó el caso real.
    /// </summary>
    private static bool MentionsTooManyRequests(string? message) =>
        message is not null &&
        (message.Contains("429", StringComparison.Ordinal) ||
         message.Contains("too many requests", StringComparison.OrdinalIgnoreCase));
}
