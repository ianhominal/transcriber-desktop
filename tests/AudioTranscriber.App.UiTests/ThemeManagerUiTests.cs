using System.Windows;
using System.Windows.Media;
using AudioTranscriber.Core.Runtime;

namespace AudioTranscriber.App.UiTests;

/// <summary>
/// Cubre <see cref="ThemeManager"/>: que Apply efectivamente reemplaza el ResourceDictionary de
/// colores activo (Colors.Light.xaml / Colors.Dark.xaml), sin duplicarlo, y que System se resuelve
/// siempre a un tema concreto (nunca queda "System" como EffectiveTheme).
/// <para/>
/// El cuerpo corre marshaleado a <see cref="UiThread"/> (ver ese archivo para el porqué) en vez de
/// usar [StaFact] directo. Application.Current es un singleton compartido por TODO el proceso de
/// test (ver <see cref="UiTestApplication"/>): cada test deja el tema en Light al terminar para no
/// filtrar estado a otros tests que corran después.
/// </summary>
public class ThemeManagerUiTests
{
    [Fact]
    public void Apply_Dark_reemplaza_el_diccionario_de_colores_por_la_paleta_oscura() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            ThemeManager.Apply(AppTheme.Light);
            var lightBg = ((SolidColorBrush)Application.Current!.FindResource("Bg")).Color;

            ThemeManager.Apply(AppTheme.Dark);
            var darkBg = ((SolidColorBrush)Application.Current!.FindResource("Bg")).Color;

            Assert.NotEqual(lightBg, darkBg);
            Assert.Equal(AppTheme.Dark, ThemeManager.CurrentSetting);
            Assert.Equal(AppTheme.Dark, ThemeManager.EffectiveTheme);

            ThemeManager.Apply(AppTheme.Light); // no filtrar estado a otros tests.
        });

    [Fact]
    public void Apply_repetido_nunca_duplica_el_diccionario_de_colores() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            ThemeManager.Apply(AppTheme.Light);
            ThemeManager.Apply(AppTheme.Dark);
            ThemeManager.Apply(AppTheme.Light);
            ThemeManager.Apply(AppTheme.Dark);

            var colorDictionaries = Application.Current!.Resources.MergedDictionaries.Count(d =>
                d.Source is not null &&
                (d.Source.OriginalString.EndsWith("Colors.Light.xaml") ||
                 d.Source.OriginalString.EndsWith("Colors.Dark.xaml")));

            Assert.Equal(1, colorDictionaries);

            ThemeManager.Apply(AppTheme.Light); // no filtrar estado a otros tests.
        });

    [Fact]
    public void Apply_System_siempre_resuelve_a_Light_o_Dark_nunca_queda_en_System() =>
        UiThread.Invoke(() =>
        {
            UiTestApplication.EnsureCreated();

            ThemeManager.Apply(AppTheme.System);

            Assert.Equal(AppTheme.System, ThemeManager.CurrentSetting);
            Assert.True(ThemeManager.EffectiveTheme is AppTheme.Light or AppTheme.Dark);

            ThemeManager.Apply(AppTheme.Light); // no filtrar estado a otros tests.
        });
}
