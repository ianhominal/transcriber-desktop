namespace AudioTranscriber.Core.Sync;

/// <summary>
/// Clasifica una falla de push del sync en PERMANENTE (nunca va a andar con estos mismos bytes) vs
/// TRANSITORIA (vale la pena reintentar el próximo ciclo).
///
/// Existe por un bug real: un audio demasiado grande para la nube (413) se reintentaba
/// indefinidamente, cada 60 s, para siempre (verificado 2026-07-17: un solo audio llevaba 127
/// intentos en un día). Una caída de red o un 500 pasajero SÍ vale la pena reintentarlos; un 413
/// por tamaño no, porque el mismo archivo siempre va a pesar lo mismo.
/// </summary>
public static class SyncFailureClassifier
{
    /// <summary>
    /// True si la falla es PERMANENTE para el contenido actual: el backend rechazó el request por
    /// algo que no cambia entre reintentos del MISMO archivo. Hoy: 413 (Payload Too Large, audio
    /// muy grande) y 415 (Unsupported Media Type). Todo lo demás (red, timeout, 5xx, 429 rate
    /// limit, 401/403 auth) es transitorio o se resuelve por otra vía: se reintenta.
    ///
    /// "Permanente para estos bytes" NO es "para siempre": si el archivo cambia (se transcribe
    /// local, se comprime, se regraba) su hash cambia y el planner lo vuelve a proponer. Ver cómo
    /// <see cref="SyncEngine"/> avanza la baseline en este caso en vez de reintentar.
    /// </summary>
    public static bool IsPermanentUploadFailure(Exception ex) =>
        ex is SyncApiException { StatusCode: 413 or 415 };
}
