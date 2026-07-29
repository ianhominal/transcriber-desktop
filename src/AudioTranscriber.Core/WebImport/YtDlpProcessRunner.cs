using System.Diagnostics;
using System.Text;

namespace AudioTranscriber.Core.WebImport;

/// <summary>
/// Implementación real de <see cref="IYtDlpProcessRunner"/>: lanza <c>yt-dlp.exe</c> como proceso
/// hijo, con STDOUT/STDERR redirigidos. STDOUT se reporta línea por línea (para el progreso de
/// descarga) y también se acumula completo (para el análisis, que necesita el JSON entero de una).
/// Al cancelar, mata el proceso -- yt-dlp no atiende Ctrl+C de forma prolija cuando se lo maneja
/// como proceso hijo redirigido, así que <c>Kill(entireProcessTree: true)</c> es más confiable que
/// esperar un cierre gracioso.
/// </summary>
public sealed class YtDlpProcessRunner : IYtDlpProcessRunner
{
    public async Task<YtDlpProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        IProgress<string>? onOutputLine,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stdout.AppendLine(e.Data);
            onOutputLine?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var registration = ct.Register(() => TryKill(process));

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new YtDlpProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // El proceso puede haber terminado solo entre el chequeo y el Kill -- no hay nada más
            // que hacer acá, ya no queda proceso que matar.
        }
    }
}
