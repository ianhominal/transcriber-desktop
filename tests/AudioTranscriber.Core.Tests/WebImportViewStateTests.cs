using AudioTranscriber.Core.WebImport;
using Xunit;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Lógica pura de estado/formateo para la ventana "Transcribir desde una URL" (App/WebImportWindow):
/// gating de los botones y formateo de duración -- separado de la UI para poder testearlo sin WPF,
/// mismo criterio que <see cref="AudioTranscriber.Core.Workspaces.BatchTranscribePlanner"/>.
/// </summary>
public class WebImportViewStateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanAnalyze_false_con_url_vacia_o_en_blanco(string? url)
    {
        Assert.False(WebImportViewState.CanAnalyze(url));
    }

    [Fact]
    public void CanAnalyze_true_con_texto_no_vacio()
    {
        Assert.True(WebImportViewState.CanAnalyze("https://example.com/video"));
    }

    [Fact]
    public void CanConfirmSelection_false_sin_items_seleccionados()
    {
        Assert.False(WebImportViewState.CanConfirmSelection(0));
    }

    [Fact]
    public void CanConfirmSelection_true_con_al_menos_un_item()
    {
        Assert.True(WebImportViewState.CanConfirmSelection(1));
        Assert.True(WebImportViewState.CanConfirmSelection(3));
    }

    [Fact]
    public void FormatDuration_null_devuelve_guion()
    {
        Assert.Equal("--:--", WebImportViewState.FormatDuration(null));
    }

    [Fact]
    public void FormatDuration_menos_de_una_hora_usa_mm_ss()
    {
        Assert.Equal("03:07", WebImportViewState.FormatDuration(TimeSpan.FromSeconds(187)));
    }

    [Fact]
    public void FormatDuration_una_hora_o_mas_usa_h_mm_ss()
    {
        Assert.Equal("1:02:03", WebImportViewState.FormatDuration(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void FormatDuration_negativa_se_trata_como_desconocida()
    {
        Assert.Equal("--:--", WebImportViewState.FormatDuration(TimeSpan.FromSeconds(-1)));
    }
}
