using AudioTranscriber.Core.Observability;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// La transcripción era el único subsistema sin log a disco: cuando una tester reportó "no me deja
/// transcribir ningún audio", no había NADA para leer. Estos tests fijan que el log traiga lo que
/// hace falta para diagnosticar sin tener la máquina delante.
/// </summary>
public class TranscriptionLogFormatterTests
{
    private static readonly DateTime Momento = new(2026, 8, 3, 14, 5, 9);

    [Fact]
    public void ElLogVaALaMismaCarpetaQueElResto()
    {
        var dir = TranscriptionLogFormatter.ResolveLogDirectory(@"C:\Users\Tester\AppData\Local");

        Assert.Equal(@"C:\Users\Tester\AppData\Local\AudioTranscriber\logs", dir);
    }

    [Fact]
    public void UnArchivoPorDia()
    {
        Assert.Equal("transcribe-20260803.log", TranscriptionLogFormatter.ResolveLogFileName(Momento));
        Assert.Equal(
            "transcribe-20261231.log",
            TranscriptionLogFormatter.ResolveLogFileName(new DateTime(2026, 12, 31)));
    }

    [Fact]
    public void ElEncabezadoTraeTodoElContextoDelIntento()
    {
        var linea = TranscriptionLogFormatter.FormatStart(
            Momento, "Reunion con Dani 1.wav", 11_219_763, "local", "large-v3", "es", true, "1.0.70");

        Assert.Contains("Reunion con Dani 1.wav", linea);
        Assert.Contains("10.7 MB", linea);
        Assert.Contains("'.wav'", linea);
        Assert.Contains("local", linea);
        Assert.Contains("large-v3", linea);
        Assert.Contains("identificarHablantes=True", linea);
        Assert.Contains("1.0.70", linea);
        Assert.Contains("INICIO", linea);
    }

    [Fact]
    public void ElEncabezadoSeparaCadaIntentoConUnaLineaEnBlanco()
    {
        var linea = TranscriptionLogFormatter.FormatStart(Momento, "a.wav", 1, "local", "small", "es", false, "1.0.70");

        Assert.StartsWith("\n", linea);
    }

    [Fact]
    public void ToleraUnArchivoSinExtension()
    {
        var linea = TranscriptionLogFormatter.FormatStart(Momento, "grabacion", 1, "local", "small", "es", false, "1.0.70");

        Assert.Contains("(sin extensión)", linea);
    }

    [Fact]
    public void CadaPasoQuedaConSuHora()
    {
        var linea = TranscriptionLogFormatter.FormatStep(Momento, "Convirtiendo audio (WAV)…");

        Assert.Equal("[2026-08-03 14:05:09] Convirtiendo audio (WAV)…\n", linea);
    }

    [Fact]
    public void ElCierreExitosoDiceCuantoTardoYCuantoTextoSalio()
    {
        var linea = TranscriptionLogFormatter.FormatSuccess(Momento, TimeSpan.FromSeconds(42.5), 1234);

        Assert.Contains("OK", linea);
        Assert.Contains("42.5s", linea);
        Assert.Contains("1234", linea);
    }

    // Cancelar es una decisión de la persona, no una falla: mezclarlas haría creer que la app se
    // rompió cuando en realidad hizo lo que le pidieron.
    [Fact]
    public void LaCancelacionSeDistingueDeUnError()
    {
        var linea = TranscriptionLogFormatter.FormatCancelled(Momento, TimeSpan.FromSeconds(6));

        Assert.Contains("CANCELADO", linea);
        Assert.DoesNotContain("ERROR", linea);
    }

    [Fact]
    public void ElErrorTraeTipoMensajeYStack()
    {
        Exception capturada;
        try
        {
            throw new InvalidOperationException("no se pudo abrir el modelo");
        }
        catch (Exception ex)
        {
            capturada = ex;
        }

        var linea = TranscriptionLogFormatter.FormatFailure(Momento, capturada);

        Assert.Contains("ERROR", linea);
        Assert.Contains("InvalidOperationException", linea);
        Assert.Contains("no se pudo abrir el modelo", linea);
        Assert.Contains("stack:", linea);
    }

    [Fact]
    public void ElErrorIncluyeLaExcepcionInterna()
    {
        var ex = new InvalidOperationException("falló la conversión", new IOException("archivo en uso"));

        var linea = TranscriptionLogFormatter.FormatFailure(Momento, ex);

        Assert.Contains("IOException", linea);
        Assert.Contains("archivo en uso", linea);
    }

    [Fact]
    public void ToleraUnaExcepcionSinStack()
    {
        var linea = TranscriptionLogFormatter.FormatFailure(Momento, new Exception("suelta"));

        Assert.Contains("(sin stack)", linea);
    }
}
