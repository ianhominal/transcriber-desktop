using System.Text.Json;
using AudioTranscriber.Core.Sync;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Cubre el contrato nuevo de jerarquía de proyectos en los DTOs de sync:
/// <c>parent_project_id</c> y <c>sync_origin</c> en el pull, <c>parent_project_id</c> en el push.
/// Backward-compatible: un pull sin esos campos (servidor viejo) no debe romper.
/// </summary>
public class SyncDtosTests
{
    [Fact]
    public void RemoteProject_Deserializa_ParentProjectId_y_SyncOrigin()
    {
        var json = """
            {
                "id": "child-1",
                "name": "Hijo",
                "parent_project_id": "parent-1",
                "sync_origin": "drive"
            }
            """;

        var project = JsonSerializer.Deserialize<RemoteProject>(json)!;

        Assert.Equal("parent-1", project.ParentProjectId);
        Assert.Equal("drive", project.SyncOrigin);
    }

    [Fact]
    public void RemoteProject_SinCamposNuevos_NoRompeYQuedanNull()
    {
        // Compat con un servidor viejo que todavía no manda estos campos.
        var json = """{ "id": "p1", "name": "Trabajo" }""";

        var project = JsonSerializer.Deserialize<RemoteProject>(json)!;

        Assert.Null(project.ParentProjectId);
        Assert.Null(project.SyncOrigin);
    }

    [Fact]
    public void ProjectUpsert_ParentProjectId_Null_SeOmiteDelJson()
    {
        // Contrato del servidor: "undefined" (campo ausente) NO toca el padre; "null" lo
        // desengancha. El desktop hoy no gestiona jerarquía, así que NUNCA debe mandar
        // "null" explícito (rompería/desengancharía una jerarquía creada del lado web).
        var upsert = new ProjectUpsert { Id = "p1", Name = "Trabajo" };

        var json = JsonSerializer.Serialize(upsert);

        Assert.DoesNotContain("parent_project_id", json);
    }

    [Fact]
    public void ProjectUpsert_ParentProjectId_Seteado_SeIncluyeEnElJson()
    {
        var upsert = new ProjectUpsert { Id = "child-1", Name = "Hijo", ParentProjectId = "parent-1" };

        var json = JsonSerializer.Serialize(upsert);

        Assert.Contains("\"parent_project_id\":\"parent-1\"", json);
    }

    [Fact]
    public void ProjectUpsert_Icon_Null_SeOmiteDelJson()
    {
        // Bug C2: el desktop no tiene UI de icono (AudioProject no tiene ese campo), así que Icon
        // siempre queda null acá. Sin [JsonIgnore(WhenWritingNull)] se mandaba "icon":null en CADA
        // push, pisando cualquier emoji/icono que el usuario haya elegido del lado web. Mismo
        // criterio que ya se aplicó a ParentProjectId arriba.
        var upsert = new ProjectUpsert { Id = "p1", Name = "Trabajo" };

        var json = JsonSerializer.Serialize(upsert);

        Assert.DoesNotContain("\"icon\"", json);
    }

    [Fact]
    public void ProjectUpsert_Icon_Seteado_SeIncluyeEnElJson()
    {
        var upsert = new ProjectUpsert { Id = "p1", Name = "Trabajo", Icon = "🎧" };

        var json = JsonSerializer.Serialize(upsert);

        Assert.Contains("\"icon\":", json);
        Assert.DoesNotContain("\"icon\":null", json);
    }
}
