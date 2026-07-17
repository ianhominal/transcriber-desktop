using AudioTranscriber.Core.Transcription;
using Whisper.net.Ggml;

namespace AudioTranscriber.Core.Tests;

public class LocalModelOptionsTests
{
    [Theory]
    [InlineData("small", GgmlType.Small)]
    [InlineData("medium", GgmlType.Medium)]
    [InlineData("large-v3", GgmlType.LargeV3)]
    public void Resolve_IdEnLaAllowlist_DevuelveElGgmlTypeCorrecto(string id, GgmlType expected)
    {
        var resolved = LocalModelOptions.Resolve(id);

        Assert.Equal(expected, resolved.Type);
        Assert.Equal(id, resolved.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("large")] // sin el "-v3": no es un id válido del catálogo
    [InlineData("Small")] // case-sensitive a propósito, mismo criterio que TranslationOptions
    [InlineData("q5_0")]
    [InlineData("cualquier-cosa")]
    public void Resolve_IdFueraDeLaAllowlistONulo_CaeAlDefault(string? id)
    {
        var resolved = LocalModelOptions.Resolve(id);

        Assert.Equal(LocalModelOptions.DefaultModelId, resolved.Id);
        Assert.Equal(GgmlType.Small, resolved.Type);
    }

    [Fact]
    public void DefaultModelId_EsSmall()
    {
        // NO cambiar: es el único modelo que existía antes de este selector -- subir el default
        // forzaría una descarga de cientos de MB no pedida a quien ya viene usando la app.
        Assert.Equal("small", LocalModelOptions.DefaultModelId);
    }

    [Fact]
    public void Resolve_CadaModeloDelCatalogoSeResuelveASiMismo()
    {
        foreach (var model in LocalModelOptions.Models)
        {
            var resolved = LocalModelOptions.Resolve(model.Id);
            Assert.Equal(model, resolved);
        }
    }

    [Fact]
    public void Models_TieneLosTresModelosConSusEtiquetasYTamanioReal()
    {
        var byId = LocalModelOptions.Models.ToDictionary(m => m.Id, m => m.Label);

        Assert.Equal(3, LocalModelOptions.Models.Count);
        // Tamaños reales verificados con HEAD a Hugging Face (carpeta q5_0 del repo
        // sandrohanea/whisper.net) -- ver LocalModelOptions.
        Assert.Equal("Rápido (167 MB)", byId["small"]);
        Assert.Equal("Bueno (514 MB)", byId["medium"]);
        Assert.Equal("El mejor (1 GB)", byId["large-v3"]);
    }

    [Fact]
    public void Models_NingunaEtiquetaExponeElNombreTecnicoDelModelo()
    {
        // Pedido explícito: nada de "small/medium/large", "q5_0" ni "cuantizado" en la cara de la
        // usuaria -- ver LocalModelOptions.Models.
        foreach (var model in LocalModelOptions.Models)
        {
            var label = model.Label.ToLowerInvariant();
            Assert.DoesNotContain("small", label);
            Assert.DoesNotContain("medium", label);
            Assert.DoesNotContain("large", label);
            Assert.DoesNotContain("q5_0", label);
            Assert.DoesNotContain("cuantizado", label);
        }
    }

    [Theory]
    [InlineData(GgmlType.Tiny, "tiny")]
    [InlineData(GgmlType.TinyEn, "tiny.en")]
    [InlineData(GgmlType.Base, "base")]
    [InlineData(GgmlType.BaseEn, "base.en")]
    [InlineData(GgmlType.Small, "small")]
    [InlineData(GgmlType.SmallEn, "small.en")]
    [InlineData(GgmlType.Medium, "medium")]
    [InlineData(GgmlType.MediumEn, "medium.en")]
    [InlineData(GgmlType.LargeV1, "large-v1")]
    [InlineData(GgmlType.LargeV2, "large-v2")]
    [InlineData(GgmlType.LargeV3, "large-v3")]
    [InlineData(GgmlType.LargeV3Turbo, "large-v3-turbo")]
    public void RemoteFileName_CoincideConElNombreRealDelArchivoEnHuggingFace(GgmlType type, string expected)
    {
        Assert.Equal(expected, LocalModelOptions.RemoteFileName(type));
    }

    [Theory]
    [InlineData(GgmlType.LargeV1)]
    [InlineData(GgmlType.LargeV2)]
    [InlineData(GgmlType.LargeV3)]
    [InlineData(GgmlType.LargeV3Turbo)]
    public void RemoteFileName_ModelosLarge_NuncaCoincideConToStringLowerInvariant(GgmlType type)
    {
        // El bug real: GgmlType.LargeV3.ToString().ToLowerInvariant() da "largev3" (sin guión), el
        // archivo real en HF es "ggml-large-v3.bin" (CON guión) -- 404 garantizado. Este test
        // prueba la propiedad general (nunca confiar en ToString()) para los cuatro modelos Large,
        // no solo el caso puntual LargeV3 que menciona el Theory de arriba.
        var buggyName = type.ToString().ToLowerInvariant();
        var realName = LocalModelOptions.RemoteFileName(type);

        Assert.NotEqual(buggyName, realName);
        Assert.Contains("-", realName);
    }

    [Fact]
    public void RemoteFileName_TipoNoMapeado_TiraEnVezDeAdivinar()
    {
        // No hay un GgmlType "sin mapear" real para probar esto contra el enum -- se castea un
        // valor fuera de rango a propósito para probar que el switch no cae silenciosamente a
        // ToString() (que es justo el bug que este mapeo existe para evitar).
        var invalid = (GgmlType)9999;

        Assert.Throws<ArgumentOutOfRangeException>(() => LocalModelOptions.RemoteFileName(invalid));
    }
}
