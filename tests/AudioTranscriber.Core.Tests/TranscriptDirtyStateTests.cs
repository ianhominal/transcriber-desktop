using AudioTranscriber.Core.Workspaces;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class TranscriptDirtyStateTests
{
    [Fact]
    public void TextoIgualAlCargado_NoEstaSucio()
    {
        Assert.False(TranscriptDirtyState.IsDirty("hola que tal", "hola que tal"));
    }

    /// EL caso: editaste y todavía no guardaste. Si esto devuelve false, se pierde el trabajo.
    [Fact]
    public void TextoEditado_EstaSucio()
    {
        Assert.True(TranscriptDirtyState.IsDirty("hola que tal", "hola que tal, todo bien"));
    }

    [Fact]
    public void BorrarTodo_EstaSucio()
    {
        Assert.True(TranscriptDirtyState.IsDirty("una reunión entera", ""));
    }

    [Fact]
    public void EscribirSobreUnEditorVacio_EstaSucio()
    {
        Assert.True(TranscriptDirtyState.IsDirty(null, "algo nuevo"));
        Assert.True(TranscriptDirtyState.IsDirty("", "algo nuevo"));
    }

    [Fact]
    public void NadaCargadoYNadaEscrito_NoEstaSucio()
    {
        Assert.False(TranscriptDirtyState.IsDirty(null, null));
        Assert.False(TranscriptDirtyState.IsDirty(null, ""));
        Assert.False(TranscriptDirtyState.IsDirty("", null));
    }

    /// Un .txt traído por el sync desde la web puede tener "\n" mientras el editor de WPF escribe
    /// "\r\n". Sin normalizar, abrir una nota sincronizada aparecería como "modificada" sin que
    /// nadie la haya tocado — y un aviso que salta cuando no corresponde es un aviso que la gente
    /// aprende a ignorar.
    [Fact]
    public void SoloDifierenLosSaltosDeLinea_NoEstaSucio()
    {
        Assert.False(TranscriptDirtyState.IsDirty("linea 1\r\nlinea 2", "linea 1\nlinea 2"));
        Assert.False(TranscriptDirtyState.IsDirty("linea 1\rlinea 2", "linea 1\nlinea 2"));
    }

    /// Pero un cambio REAL en un texto multilínea sí tiene que detectarse.
    [Fact]
    public void CambioRealEnTextoMultilinea_EstaSucio()
    {
        Assert.True(TranscriptDirtyState.IsDirty("linea 1\r\nlinea 2", "linea 1\nlinea 2 editada"));
    }

    /// Los espacios NO se normalizan a propósito: borrar un espacio es una edición real.
    [Fact]
    public void UnEspacioDeMas_EstaSucio()
    {
        Assert.True(TranscriptDirtyState.IsDirty("hola", "hola "));
    }

    [Fact]
    public void DistingueMayusculas()
    {
        Assert.True(TranscriptDirtyState.IsDirty("hola", "Hola"));
    }

    /// Caso real: el texto diarizado con etiquetas de hablante.
    [Fact]
    public void FuncionaConTranscripcionesConHablantes()
    {
        const string cargado = "Persona 1: hola que tal\r\n\r\nPersona 2: todo bien";

        Assert.False(TranscriptDirtyState.IsDirty(cargado, "Persona 1: hola que tal\n\nPersona 2: todo bien"));
        Assert.True(TranscriptDirtyState.IsDirty(cargado, "Persona 1: hola, ¿qué tal?\n\nPersona 2: todo bien"));
    }
}
