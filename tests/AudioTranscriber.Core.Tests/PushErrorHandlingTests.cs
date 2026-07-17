using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Cubre la lógica pura de <see cref="PushErrorHandling"/>: interpretar `errors[]` de
/// POST /api/sync/push (strings planos, sin código/tipo estructurado -- ver contrato en
/// api/sync/push/route.ts) para detectar el rechazo de un borrado en cascada de un proyecto con
/// subproyectos/transcripciones (bug C1), y decidir si ese ítem debe reintentarse en el próximo
/// ciclo o resolverse como "necesita acción manual".
/// </summary>
public class PushErrorHandlingTests
{
    [Fact]
    public void TryParseCascadeDeleteRejection_MensajeExacto_DetectaYExtraeId()
    {
        const string error = "El proyecto abc-123 tiene 2 subproyecto(s) y 5 transcripción(es); confirmá el borrado desde la web.";

        var rejection = PushErrorHandling.TryParseCascadeDeleteRejection(error);

        Assert.NotNull(rejection);
        Assert.Equal("abc-123", rejection!.ProjectId);
        Assert.Equal(2, rejection.ChildProjectCount);
        Assert.Equal(5, rejection.TranscriptionCount);
    }

    [Fact]
    public void TryParseCascadeDeleteRejection_ProyectoInvalido_NoDetecta()
    {
        var rejection = PushErrorHandling.TryParseCascadeDeleteRejection("Proyecto inválido: (sin id)");

        Assert.Null(rejection);
    }

    [Fact]
    public void TryParseCascadeDeleteRejection_ErrorSqlDeBorrado_NoDetecta()
    {
        var rejection = PushErrorHandling.TryParseCascadeDeleteRejection("Borrar proyecto xyz-789: some sql error");

        Assert.Null(rejection);
    }

    [Fact]
    public void TryParseCascadeDeleteRejection_NullOVacio_NoDetecta()
    {
        Assert.Null(PushErrorHandling.TryParseCascadeDeleteRejection(null));
        Assert.Null(PushErrorHandling.TryParseCascadeDeleteRejection(""));
    }

    [Fact]
    public void TryParseCascadeDeleteRejection_OtroErrorDeTranscripcion_NoDetecta()
    {
        var rejection = PushErrorHandling.TryParseCascadeDeleteRejection("Transcripción t1: alguna cosa falló");

        Assert.Null(rejection);
    }

    [Fact]
    public void ShouldSkipRetry_ConRechazoDetectado_True()
    {
        var rejection = new CascadeDeleteRejection("abc-123", 2, 5);

        Assert.True(PushErrorHandling.ShouldSkipRetry(rejection));
    }

    [Fact]
    public void ShouldSkipRetry_SinRechazo_False()
    {
        // Cualquier otro error (transitorio, red, bug del servidor) mantiene el comportamiento de
        // reintento normal que ya tiene el resto del sync -- no se toca nada para esos casos.
        Assert.False(PushErrorHandling.ShouldSkipRetry(null));
    }
}
