namespace AudioTranscriber.Core.Workspaces;

/// <summary>
/// ¿El texto que está en pantalla tiene cambios sin guardar?
///
/// Existe por el hallazgo más caro de la revisión de diseño 2026-07-16, verificado contra el código:
/// `OnSelectedAudioChanged` pisaba el texto del editor con `File.ReadAllText` del audio nuevo, SIN
/// chequeo ni aviso. Corregías veinte minutos de una reunión, clickeabas otro audio en la lista, y
/// se iba todo. Sin autosave, sin Ctrl+S, sin flag. Un click común en el árbol llegaba por ahí.
///
/// El criterio (mismo idiom que `AlreadyPolished` ya usa en MainViewModel, y por la misma razón):
/// se guarda el texto EXACTO que se cargó del disco y se compara contra el actual. Un booleano
/// aparte que alguien tiene que acordarse de prender y apagar se desincroniza; una comparación no.
/// </summary>
public static class TranscriptDirtyState
{
    /// <summary>
    /// Hay cambios sin guardar. <paramref name="loaded"/> es el texto tal cual salió del disco
    /// (o null si nunca se cargó nada).
    ///
    /// Normaliza los saltos de línea antes de comparar: el editor de WPF escribe "\r\n" y un .txt
    /// guardado en otra plataforma (o traído por el sync desde la web) puede tener "\n". Sin esto,
    /// abrir una nota sincronizada aparecería como "modificada" sin que nadie la tocara, y el aviso
    /// de "tenés cambios sin guardar" pasaría a ser ruido que la gente aprende a ignorar — que es
    /// peor que no tenerlo.
    /// </summary>
    public static bool IsDirty(string? loaded, string? current)
    {
        var a = Normalize(loaded);
        var b = Normalize(current);
        return !string.Equals(a, b, System.StringComparison.Ordinal);
    }

    private static string Normalize(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
}
