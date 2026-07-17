using System.Collections.Generic;
using System.Linq;
using AudioTranscriber.Core.Common;
using Xunit;

namespace AudioTranscriber.Core.Tests;

/// <summary>
/// Verifica el CONTRASTE REAL de la paleta, leyendo Colors.Dark.xaml y Colors.Light.xaml del disco.
///
/// Por qué existe (revisión de diseño 2026-07-16): la app tenía las reglas correctas escritas en
/// comentarios y nada que las hiciera cumplir. Medido: el acento como texto daba 2.94:1, el anillo
/// de foco 1.96:1, los deshabilitados 1.70:1. Todo eso entró sin ruido.
///
/// Cada par de acá corresponde a texto que la app REALMENTE pinta sobre una superficie que la app
/// REALMENTE usa. Si agregás un par de color nuevo a la UI, agregalo también acá.
/// </summary>
public class PaletteContrastTests
{
    public static TheoryData<string> Themes => new() { "dark", "light" };

    private static ThemePalette Palette(string theme) =>
        theme == "dark" ? ThemePalette.Dark() : ThemePalette.Light();

    /// <summary>Las superficies sobre las que la app pinta texto.</summary>
    private static readonly string[] TextSurfaces = ["Bg", "Surface", "SurfaceAlt"];

    private static void AssertContrast(ThemePalette p, string fg, string bg, double min)
    {
        var ratio = ColorContrast.Ratio(p.Hex(fg), p.Hex(bg));
        Assert.True(ratio >= min,
            $"[{p.Name}] {fg} ({p.Hex(fg)}) sobre {bg} ({p.Hex(bg)}) = {ratio:F2}:1, mínimo {min}:1.");
    }

    // ---- Texto ----

    [Theory]
    [MemberData(nameof(Themes))]
    public void TextPrimary_CumpleAA_SobreTodaSuperficie(string theme)
    {
        var p = Palette(theme);
        foreach (var s in TextSurfaces)
            AssertContrast(p, "TextPrimary", s, ColorContrast.AaNormalText);
    }

    /// <summary>
    /// Los nombres de hablante de la vista "Leer" (Speaker0..5 + SpeakerNeutral) son texto chico
    /// sobre Surface (el fondo del panel del transcript). Tienen que cumplir AA 4.5:1, igual que
    /// los que definió la web para su vista Leer.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void ColoresDeHablante_CumplenAA_SobreSurface(string theme)
    {
        var p = Palette(theme);
        string[] speakers = ["Speaker0", "Speaker1", "Speaker2", "Speaker3", "Speaker4", "Speaker5", "SpeakerNeutral"];
        foreach (var c in speakers)
            AssertContrast(p, c, "Surface", ColorContrast.AaNormalText);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TextSecondary_CumpleAA_SobreTodaSuperficie(string theme)
    {
        var p = Palette(theme);
        foreach (var s in TextSurfaces)
            AssertContrast(p, "TextSecondary", s, ColorContrast.AaNormalText);
    }

    /// TextMuted pinta tamaños de archivo, timestamps y estados: es texto normal, no decoración.
    [Theory]
    [MemberData(nameof(Themes))]
    public void TextMuted_CumpleAA_SobreTodaSuperficie(string theme)
    {
        var p = Palette(theme);
        foreach (var s in TextSurfaces)
            AssertContrast(p, "TextMuted", s, ColorContrast.AaNormalText);
    }

    /// AccentSoft es fondo de banner/chip: el texto que va encima también tiene que leerse.
    [Theory]
    [MemberData(nameof(Themes))]
    public void TextMuted_CumpleAA_SobreAccentSoft(string theme)
    {
        AssertContrast(Palette(theme), "TextMuted", "AccentSoft", ColorContrast.AaNormalText);
    }

    /// El botón primario tiene que leerse en SUS TRES ESTADOS, no solo en reposo. El hover no es un
    /// detalle: es el estado en el que está justo cuando lo vas a apretar.
    [Theory]
    [MemberData(nameof(Themes))]
    public void TextOnAccent_CumpleAA_EnReposoHoverYPressed(string theme)
    {
        var p = Palette(theme);
        foreach (var estado in new[] { "Accent", "AccentHover", "AccentPressed" })
            AssertContrast(p, "TextOnAccent", estado, ColorContrast.AaNormalText);
    }

    /// Los tres estados tienen que ser DISTINGUIBLES entre sí. Un fix ingenuo de contraste (mandar
    /// el hover al valor del pressed) los colapsa: el botón pierde el feedback de click, o sea que
    /// se destruye una affordance que funciona para ganar unas décimas de ratio.
    [Theory]
    [MemberData(nameof(Themes))]
    public void LosTresEstadosDelAcento_SeDistinguenEntreSi(string theme)
    {
        var p = Palette(theme);
        var estados = new[] { "Accent", "AccentHover", "AccentPressed" };

        foreach (var (a, b) in new[] { (0, 1), (1, 2), (0, 2) })
        {
            Assert.True(p.Hex(estados[a]) != p.Hex(estados[b]),
                $"[{p.Name}] {estados[a]} y {estados[b]} son el MISMO color ({p.Hex(estados[a])}): " +
                "el botón pierde el feedback de estado.");

            // Y no solo distintos: perceptiblemente distintos.
            var delta = ColorContrast.Ratio(p.Hex(estados[a]), p.Hex(estados[b]));
            Assert.True(delta >= 1.1,
                $"[{p.Name}] {estados[a]} ({p.Hex(estados[a])}) y {estados[b]} ({p.Hex(estados[b])}) " +
                $"casi no se distinguen ({delta:F2}:1).");
        }
    }


    // ---- Acento como TEXTO (no como relleno de botón) ----

    /// El acento se usa como color de TEXTO en banners y links (MainWindow:78, 409, 412, 445, 567,
    /// 585 + Inputs:86). Un color pensado para rellenar un botón no sirve automáticamente como
    /// tinta: hay que verificarlo aparte.
    [Theory]
    [MemberData(nameof(Themes))]
    public void AccentText_CumpleAA_SobreSuperficieYAccentSoft(string theme)
    {
        var p = Palette(theme);
        Assert.True(p.Has("AccentText"),
            $"[{p.Name}] falta 'AccentText'. Toda clave nueva va en los DOS diccionarios: si va en " +
            "uno solo, en el otro tema el DynamicResource no resuelve, WPF no tira excepción, y el " +
            "texto hereda el color del padre. Falla silenciosa.");

        AssertContrast(p, "AccentText", "Surface", ColorContrast.AaNormalText);
        AssertContrast(p, "AccentText", "AccentSoft", ColorContrast.AaNormalText);
    }

    // ---- Foco de teclado ----

    /// El anillo de foco es un componente de interfaz: mínimo 3:1. Y se mide CON su Opacity, que es
    /// lo que el ojo ve — el bug original fue declarar un color que contrasta bien y arruinarlo con
    /// Opacity=0.55 (quedaba en 1.96:1).
    [Theory]
    [MemberData(nameof(Themes))]
    public void FocusRing_EsVisible_SobreTodaSuperficie(string theme)
    {
        var p = Palette(theme);
        foreach (var s in TextSurfaces)
        {
            var ratio = ColorContrast.RatioWithOpacity(p.Hex("FocusRing"), p.Opacity("FocusRing"), p.Hex(s));
            Assert.True(ratio >= ColorContrast.AaLargeTextOrUi,
                $"[{p.Name}] FocusRing ({p.Hex("FocusRing")} @ {p.Opacity("FocusRing")}) sobre {s} " +
                $"({p.Hex(s)}) = {ratio:F2}:1, mínimo {ColorContrast.AaLargeTextOrUi}:1. " +
                "Un anillo de foco que no se ve no es accesibilidad, es decoración.");
        }
    }

    // ---- Deshabilitado ----

    /// Un control deshabilitado tiene que LEERSE como deshabilitado, no desaparecer. "Carpeta de
    /// trabajo" está deshabilitado durante toda transcripción y grabación: a 1.70:1 no está
    /// apagado, está ausente.
    [Theory]
    [MemberData(nameof(Themes))]
    public void TextoDeshabilitado_SeLee_SobreSuperficieDeshabilitada(string theme)
    {
        var p = Palette(theme);
        Assert.True(p.Has("DisabledTextOnDisabledSurface"),
            $"[{p.Name}] falta 'DisabledTextOnDisabledSurface'. NO reuses 'DisabledText': esa clave " +
            "también gobierna el texto de inputs deshabilitados sobre SurfaceAlt (Inputs.xaml:29, :63), " +
            "y oscurecerla haría que un input deshabilitado se lea como habilitado.");

        AssertContrast(p, "DisabledTextOnDisabledSurface", "DisabledSurface", ColorContrast.AaNormalText);
    }

    /// <summary>
    /// A propósito NO se le exige contraste a `DisabledText` (el texto de inputs deshabilitados
    /// sobre SurfaceAlt, hoy 2.14:1 en tema claro), por dos razones:
    ///
    /// 1. WCAG 2.1 (criterio 1.4.3) EXIME explícitamente a los componentes de interfaz inactivos.
    /// 2. En un input deshabilitado, el estado ya lo comunica el FONDO (SurfaceAlt en vez de
    ///    Surface), no solo la tinta. Oscurecer el texto lo haría parecer habilitado — que es el
    ///    problema opuesto y peor.
    ///
    /// El caso del BOTÓN deshabilitado con relleno es distinto y sí se verifica (ver
    /// `TextoDeshabilitado_SeLee_SobreSuperficieDeshabilitada`): ahí el argumento no es WCAG sino
    /// uso real — "Carpeta de trabajo" queda deshabilitado durante toda transcripción y grabación,
    /// y a 1.70:1 no se leía como apagado, no se leía y punto.
    ///
    /// Este test existe para DEJAR ESCRITA la diferencia: la primera versión exigía 3:1 acá y
    /// fallaba. El test estaba mal, no la paleta.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TextoDeshabilitadoDeInputs_SeDistingueDelHabilitado(string theme)
    {
        var p = Palette(theme);

        // Lo que importa no es el contraste contra el fondo, sino que se DISTINGA del texto normal.
        var deshabilitado = ColorContrast.Ratio(p.Hex("DisabledText"), p.Hex("SurfaceAlt"));
        var habilitado = ColorContrast.Ratio(p.Hex("TextPrimary"), p.Hex("SurfaceAlt"));

        Assert.True(habilitado > deshabilitado * 1.5,
            $"[{p.Name}] el texto deshabilitado ({deshabilitado:F2}:1) no se distingue lo suficiente " +
            $"del habilitado ({habilitado:F2}:1) sobre SurfaceAlt.");
    }

    // ---- Peligro ----

    /// El botón de borrar tiene que leerse en sus tres estados, no solo en reposo.
    [Theory]
    [MemberData(nameof(Themes))]
    public void TextoSobreDanger_CumpleAA_EnReposoYHover(string theme)
    {
        var p = Palette(theme);
        Assert.True(p.Has("TextOnDanger"), $"[{p.Name}] falta 'TextOnDanger' (va en los DOS diccionarios).");

        AssertContrast(p, "TextOnDanger", "Danger", ColorContrast.AaNormalText);
        AssertContrast(p, "TextOnDanger", "DangerHover", ColorContrast.AaNormalText);
    }

    // ---- Paridad entre temas ----

    /// El bug más caro de esta paleta no es un ratio: es una clave que existe en un tema y no en el
    /// otro. ThemeManager swapea el diccionario ENTERO (ThemeManager.cs:60). Una clave faltante no
    /// tira excepción: el control hereda el color del padre y nadie se entera hasta producción.
    [Fact]
    public void LosDosTemasDefinenExactamenteLasMismasClaves()
    {
        var dark = ThemePalette.Dark().Keys.OrderBy(k => k).ToList();
        var light = ThemePalette.Light().Keys.OrderBy(k => k).ToList();

        var soloDark = dark.Except(light).ToList();
        var soloLight = light.Except(dark).ToList();

        Assert.True(soloDark.Count == 0 && soloLight.Count == 0,
            $"Claves solo en dark: [{string.Join(", ", soloDark)}]. " +
            $"Claves solo en light: [{string.Join(", ", soloLight)}].");
    }

    /// Los 12 colores de proyecto tienen que estar en los dos temas (los consume un converter por
    /// nombre dinámico: si falta uno, ese proyecto pierde el color sin ruido).
    [Theory]
    [MemberData(nameof(Themes))]
    public void LosDoceColoresDeProyecto_Existen(string theme)
    {
        var p = Palette(theme);
        string[] ids = ["Red", "Orange", "Amber", "Green", "Teal", "Cyan", "Blue", "Indigo", "Violet", "Purple", "Pink", "Rose"];
        foreach (var id in ids)
            Assert.True(p.Has($"Project{id}"), $"[{p.Name}] falta Project{id}.");
    }
}
