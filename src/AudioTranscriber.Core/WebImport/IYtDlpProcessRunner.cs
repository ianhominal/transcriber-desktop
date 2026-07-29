namespace AudioTranscriber.Core.WebImport;

/// <summary>Resultado crudo de correr yt-dlp: exit code + toda la salida capturada.</summary>
public sealed record YtDlpProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Abstrae la ejecución del proceso <c>yt-dlp.exe</c> para poder testear <see cref="WebPageAnalyzer"/>
/// y <see cref="WebAudioDownloader"/> con dobles de prueba -- SIN lanzar procesos reales ni tocar
/// red en los tests. La implementación real es <see cref="YtDlpProcessRunner"/>.
/// </summary>
public interface IYtDlpProcessRunner
{
    /// <summary>
    /// Corre <paramref name="executablePath"/> con <paramref name="arguments"/> y espera a que
    /// termine. <paramref name="onOutputLine"/>, si se pasa, recibe cada línea de STDOUT a medida
    /// que se produce (se usa para el progreso de descarga; en el análisis no hace falta y se pasa
    /// <c>null</c>). Cancelar <paramref name="ct"/> debe matar el proceso.
    /// </summary>
    Task<YtDlpProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IProgress<string>? onOutputLine,
        CancellationToken ct);
}
