using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AudioTranscriber.Core.Runtime;
using AudioTranscriber.Core.Workspaces;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Cubre la paleta "color por proyecto" (F2, ver changelog): que las 12 claves de brush
/// <c>Project{Id}</c> definidas en Colors.Light.xaml/Colors.Dark.xaml estén en sync con
/// <see cref="ProjectColorPalette.Ids"/> (única fuente de verdad de los ids válidos, en Core, sin
/// dependencia de WPF) en AMBOS temas, y que <see cref="ProjectColorToBrushConverter"/> nunca tire
/// ante un id null/vacío/desconocido -- necesita WPF real (Application.Current.Resources), por eso
/// vive acá y no en AudioTranscriber.Core.Tests.
/// <para/>
/// El cuerpo corre marshaleado a <see cref="UiThread"/> (ver ese archivo para el porqué) en vez de
/// usar [StaFact] directo. Application.Current es un singleton compartido por TODO el proceso de
/// test (ver <see cref="UiTestApplication"/>): cada test que toca el tema lo deja en Light al
/// terminar, mismo criterio que <see cref="ThemeManagerUiTests"/>.
/// </summary>
public class ProjectColorPaletteUiTests
{
    [Fact]
    public void Todos_los_ids_de_la_paleta_tienen_su_brush_ProjectXxx_en_ambos_temas() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeManager.Apply(theme);
                foreach (var id in ProjectColorPalette.Ids)
                {
                    var key = "Project" + char.ToUpperInvariant(id[0]) + id[1..];
                    var brush = Application.Current!.TryFindResource(key);
                    Assert.True(brush is SolidColorBrush, $"Falta o tiene el tipo incorrecto: {key} (tema {theme}).");
                }
            }

            ThemeManager.Apply(AppTheme.Light); // no filtrar estado a otros tests.
        });

    [Fact]
    public void ProjectColorToBrushConverter_id_valido_resuelve_el_brush_correspondiente() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            ThemeManager.Apply(AppTheme.Light);
            var converter = new ProjectColorToBrushConverter();

            var resolved = converter.Convert("indigo", typeof(Brush), null, CultureInfo.InvariantCulture);

            var expected = Application.Current!.TryFindResource("ProjectIndigo");
            Assert.Equal(expected, resolved);
        });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-existe-en-la-paleta")]
    [InlineData(42)]
    public void ProjectColorToBrushConverter_valores_invalidos_nunca_tiran_y_caen_a_transparente(object? value) =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();
            var converter = new ProjectColorToBrushConverter();

            var exception = Record.Exception(() =>
                converter.Convert(value!, typeof(Brush), null, CultureInfo.InvariantCulture));
            Assert.Null(exception);

            var resolved = converter.Convert(value!, typeof(Brush), null, CultureInfo.InvariantCulture);
            Assert.Equal(Brushes.Transparent, resolved);
        });
}
