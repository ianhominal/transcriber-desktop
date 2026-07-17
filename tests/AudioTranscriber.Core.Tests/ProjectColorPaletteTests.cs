using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.Core.Tests;

public class ProjectColorPaletteTests
{
    // Orden fijo del catálogo (F2 del sibling web) -- no se re-deriva, se fija literal acá para
    // que un reorder accidental de ProjectColorPalette.Ids rompa el test (el orden importa: es el
    // orden del picker en la UI).
    private static readonly string[] ExpectedIdsInOrder =
    {
        "red", "orange", "amber", "green", "teal", "cyan",
        "blue", "indigo", "violet", "purple", "pink", "rose",
    };

    [Fact]
    public void Ids_tiene_los_12_ids_en_el_orden_exacto_del_catalogo()
    {
        Assert.Equal(ExpectedIdsInOrder, ProjectColorPalette.Ids);
    }

    [Fact]
    public void Ids_no_tiene_duplicados()
    {
        Assert.Equal(ProjectColorPalette.Ids.Count, ProjectColorPalette.Ids.Distinct().Count());
    }

    [Theory]
    [InlineData("red")]
    [InlineData("orange")]
    [InlineData("amber")]
    [InlineData("green")]
    [InlineData("teal")]
    [InlineData("cyan")]
    [InlineData("blue")]
    [InlineData("indigo")]
    [InlineData("violet")]
    [InlineData("purple")]
    [InlineData("pink")]
    [InlineData("rose")]
    public void IsValid_true_para_cada_id_del_catalogo(string id)
    {
        Assert.True(ProjectColorPalette.IsValid(id));
        Assert.Equal(id, ProjectColorPalette.Normalize(id));
    }

    [Fact]
    public void IsValid_false_para_null()
    {
        Assert.False(ProjectColorPalette.IsValid(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Red")] // case-sensitive: "Red" (mayúscula) no es el id persistido "red"
    [InlineData("magenta")] // no existe en la paleta
    [InlineData("  red  ")] // no se trimea: el id persistido nunca debería traer espacios
    public void IsValid_false_para_ids_desconocidos_o_mal_formados(string id)
    {
        Assert.False(ProjectColorPalette.IsValid(id));
    }

    [Fact]
    public void Normalize_nunca_tira_y_cae_a_null_ante_id_null_vacio_o_desconocido()
    {
        Assert.Null(ProjectColorPalette.Normalize(null));
        Assert.Null(ProjectColorPalette.Normalize(string.Empty));
        Assert.Null(ProjectColorPalette.Normalize("no-existe-en-la-paleta"));
    }
}
