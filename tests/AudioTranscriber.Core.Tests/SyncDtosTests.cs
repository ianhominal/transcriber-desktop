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

    // ---- Task 2.3/2.4 (ADR-07): version monotónico en el pull ---------------------------------

    [Fact]
    public void RemoteProject_Deserializa_Version()
    {
        var json = """{ "id": "p1", "name": "Trabajo", "version": 5 }""";

        var project = JsonSerializer.Deserialize<RemoteProject>(json)!;

        Assert.Equal(5, project.Version);
    }

    [Fact]
    public void RemoteTranscription_Deserializa_Version()
    {
        var json = """{ "id": "t1", "audio_name": "nota.mp3", "version": 3 }""";

        var transcription = JsonSerializer.Deserialize<RemoteTranscription>(json)!;

        Assert.Equal(3, transcription.Version);
    }

    // ---- Task 2.5/2.6 (ADR-07c/g): base_version en el push -------------------------------------
    // Mismo criterio que ParentProjectId/Icon arriba: "undefined" (campo ausente) es distinto de
    // "0" -- un item NUEVO (nunca sincronizado) no tiene base_version que comparar, así que se
    // omite del JSON en vez de mandar un 0 falso que el servidor interpretaría como una versión real.

    [Fact]
    public void ProjectUpsert_BaseVersion_Null_SeOmiteDelJson()
    {
        var upsert = new ProjectUpsert { Id = "p1", Name = "Trabajo" };

        var json = JsonSerializer.Serialize(upsert);

        Assert.DoesNotContain("base_version", json);
    }

    [Fact]
    public void ProjectUpsert_BaseVersion_Seteado_SeIncluyeEnElJson()
    {
        var upsert = new ProjectUpsert { Id = "p1", Name = "Trabajo", BaseVersion = 4 };

        var json = JsonSerializer.Serialize(upsert);

        Assert.Contains("\"base_version\":4", json);
    }

    [Fact]
    public void TranscriptionUpsert_BaseVersion_Null_SeOmiteDelJson()
    {
        var upsert = new TranscriptionUpsert { Id = "t1", AudioName = "nota.mp3" };

        var json = JsonSerializer.Serialize(upsert);

        Assert.DoesNotContain("base_version", json);
    }

    [Fact]
    public void TranscriptionUpsert_BaseVersion_Seteado_SeIncluyeEnElJson()
    {
        var upsert = new TranscriptionUpsert { Id = "t1", AudioName = "nota.mp3", BaseVersion = 2 };

        var json = JsonSerializer.Serialize(upsert);

        Assert.Contains("\"base_version\":2", json);
    }

    // ---- Task 5.4 (ADR-07c): results[] estructurado en la respuesta del push ------------------
    // Reemplaza el matcheo por string de errors[] como canal de decisión (ver PushResponse) para
    // los tres casos que ya devuelve el backend (web/src/app/api/sync/push/route.ts): "ok",
    // "conflict" (con la version del server) y "error" (con un code, ej. client_too_old).

    [Fact]
    public void PushResponse_Deserializa_ResultsConLosTresEstados()
    {
        var json = """
            {"serverTime":"2026-07-28T00:00:00Z","ok":true,"errors":[],
             "results":[
               {"id":"p1","kind":"project","status":"ok","version":8},
               {"id":"t1","kind":"transcription","status":"conflict","version":11},
               {"id":"p2","kind":"project","status":"error","code":"client_too_old"}
             ]}
            """;

        var response = JsonSerializer.Deserialize<PushResponse>(json)!;

        Assert.Equal(3, response.Results.Count);

        Assert.Equal("p1", response.Results[0].Id);
        Assert.Equal("project", response.Results[0].Kind);
        Assert.Equal("ok", response.Results[0].Status);
        Assert.Equal(8, response.Results[0].Version);

        Assert.Equal("t1", response.Results[1].Id);
        Assert.Equal("transcription", response.Results[1].Kind);
        Assert.Equal("conflict", response.Results[1].Status);
        Assert.Equal(11, response.Results[1].Version);

        Assert.Equal("error", response.Results[2].Status);
        Assert.Equal("client_too_old", response.Results[2].Code);
        Assert.Null(response.Results[2].Version);
    }

    [Fact]
    public void PushResponse_SinResults_QuedaListaVacia()
    {
        // Compat con un servidor viejo (o una respuesta de test) que todavía no manda "results".
        var json = """{"serverTime":"2026-07-28T00:00:00Z","ok":true,"errors":[]}""";

        var response = JsonSerializer.Deserialize<PushResponse>(json)!;

        Assert.Empty(response.Results);
    }

    // ---- Task 11.2 (ADR-13): test de contrato C# sobre el fixture compartido ------------------
    // El fixture (openspec/changes/team-sharing/fixtures/push-response.json) es la ÚNICA fuente de
    // verdad de la forma de un ítem de results[]; el mismo fixture lo lee también
    // web/src/lib/sync/pushResponseContract.test.ts (Task 11.3). Ninguno de los dos lados hardcodea
    // el otro -- si el contrato real (web/src/app/api/sync/push/route.ts) driftea, alguno de los
    // dos tests se rompe.

    private sealed class FixtureCase
    {
        public string Name { get; set; } = "";
        public FixtureResult Result { get; set; } = new();
    }

    private sealed class FixtureResult
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Status { get; set; } = "";
        public int? Version { get; set; }
        public string? Code { get; set; }
    }

    private sealed class FixtureFile
    {
        public List<FixtureCase> Cases { get; set; } = new();
    }

    [Fact]
    public void PushResponseFixture_Deserializa_LosCuatroCasosDelContrato()
    {
        var fixturePath = FindFixturePath();
        var fixtureJson = File.ReadAllText(fixturePath);
        var fixture = JsonSerializer.Deserialize<FixtureFile>(
            fixtureJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(4, fixture.Cases.Count);

        // El wire format real (PushResultItem/PushResponse.Results) se arma con los MISMOS
        // "result" del fixture -- si el shape de PushResultItem alguna vez se desalinea del
        // fixture (ej. un campo renombrado), la deserialización de acá lo detecta. camelCase acá
        // porque PushResultItem usa [JsonPropertyName] en minúscula ("id", "kind", ...) y el
        // Serialize de acá NO hereda las opciones case-insensitive usadas para leer el fixture.
        var resultsJson = JsonSerializer.Serialize(
            fixture.Cases.Select(c => c.Result), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var responseJson = $$"""{"serverTime":"2026-07-28T00:00:00Z","ok":true,"errors":[],"results":{{resultsJson}}}""";
        var response = JsonSerializer.Deserialize<PushResponse>(responseJson)!;

        Assert.Equal(fixture.Cases.Count, response.Results.Count);
        for (var i = 0; i < fixture.Cases.Count; i++)
        {
            var expected = fixture.Cases[i].Result;
            var actual = response.Results[i];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.Code, actual.Code);
        }
    }

    /// <summary>
    /// El fixture vive DENTRO de este repo (<c>tests/AudioTranscriber.Core.Tests/Fixtures/</c>) y el
    /// csproj lo copia al directorio de salida. Antes se leía de <c>openspec/</c>, en la carpeta
    /// contenedora <c>Audio-Transcriber/</c>, que NO es un repo git: el test pasaba solo en la
    /// máquina del dueño y fallaba en cualquier clon limpio.
    ///
    /// La contraparte (<c>web/src/lib/sync/__fixtures__/push-response.json</c>) es una COPIA
    /// deliberada: son dos repos git separados, así que no hay forma de compartir un archivo sin
    /// submódulos. Si cambia el contrato, hay que actualizar las dos -- mismo criterio que los caps
    /// de <c>ai_usage_log</c>, duplicados entre <c>aiUsage.ts</c> y las migraciones SQL. Un drift
    /// real igual rompe el test en el repo que quedó viejo, porque cada lado corre su propia lógica
    /// contra el fixture en vez de hardcodear la respuesta del otro.
    /// </summary>
    private static string FindFixturePath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Fixtures", "push-response.json");
        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException(
            "No se encontró 'Fixtures/push-response.json' en " + AppContext.BaseDirectory +
            ". Revisá que el csproj siga copiando 'Fixtures\\*.json' al directorio de salida.");
    }
}
