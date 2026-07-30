using AudioTranscriber.Core.Ui;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Peso legible de un audio en la lista. El caso que motivó extraer esto de la UI: una nota que
/// llegó por sync (proyecto compartido) sin el archivo de audio en disco mostraba "0 KB" —
/// información FALSA. No es que el audio pese cero: no está en esta PC.
/// </summary>
public class AudioSizeFormatterTests
{
    [Fact]
    public void Sin_audio_local_no_muestra_un_peso_de_cero()
    {
        Assert.Equal("Sin audio local", AudioSizeFormatter.Format(0, hasAudio: false));
    }

    [Fact]
    public void Sin_audio_local_ignora_cualquier_cantidad_de_bytes_que_le_pasen()
    {
        // Defensa: si algún camino dejara SizeBytes con basura mientras HasAudio es false, el texto
        // sigue siendo honesto — no hay archivo acá, no hay peso que mostrar.
        Assert.Equal("Sin audio local", AudioSizeFormatter.Format(5_000_000, hasAudio: false));
    }

    [Fact]
    public void Un_archivo_local_realmente_vacio_si_muestra_cero_KB()
    {
        // Distinto del caso de arriba: acá el archivo EXISTE y pesa 0 bytes. "0 KB" es la verdad.
        Assert.Equal("0 KB", AudioSizeFormatter.Format(0, hasAudio: true));
    }

    [Theory]
    [InlineData(1024, "1 KB")]
    [InlineData(2048, "2 KB")]
    [InlineData(700 * 1024, "700 KB")]
    public void Menos_de_un_mega_se_muestra_en_KB(long bytes, string expected)
    {
        Assert.Equal(expected, AudioSizeFormatter.Format(bytes, hasAudio: true));
    }

    [Theory]
    [InlineData(1024 * 1024, "1,0 MB")]
    [InlineData(25 * 1024 * 1024, "25,0 MB")]
    public void Desde_un_mega_se_muestra_en_MB_con_coma_decimal(long bytes, string expected)
    {
        // Coma, no punto: la UI alrededor de este número está en español, mismo criterio explícito
        // que EngineSelector.FormatSize (que ya fuerza es-AR para no escribir "27.3 MB" en una
        // oración en español cuando Windows está en inglés).
        Assert.Equal(expected, AudioSizeFormatter.Format(bytes, hasAudio: true));
    }

    [Fact]
    public void El_limite_del_mega_no_se_cuenta_dos_veces()
    {
        // Justo por debajo de 1 MiB sigue siendo KB (y no "1024 KB" redondeado a MB).
        Assert.Equal("1024 KB", AudioSizeFormatter.Format(1024 * 1024 - 1, hasAudio: true));
    }
}
