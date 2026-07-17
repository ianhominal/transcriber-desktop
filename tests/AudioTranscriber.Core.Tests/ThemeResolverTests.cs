using AudioTranscriber.Core.Runtime;

namespace AudioTranscriber.Core.Tests;

public class ThemeResolverTests
{
    [Theory]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("System", AppTheme.System)]
    [InlineData("light", AppTheme.Light)]
    [InlineData("DARK", AppTheme.Dark)]
    public void Parse_ReconoceLosValoresValidosSinImportarMayusculas(string value, AppTheme expected)
    {
        var result = ThemeResolver.Parse(value);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nube")] // valor desconocido / corrupto
    [InlineData("  ")]
    public void Parse_CaeASystemConValoresDesconocidosOVacios(string? value)
    {
        var result = ThemeResolver.Parse(value);

        Assert.Equal(AppTheme.System, result);
    }

    [Fact]
    public void ResolveEffective_Light_SiempreDevuelveLight()
    {
        Assert.Equal(AppTheme.Light, ThemeResolver.ResolveEffective(AppTheme.Light, systemIsDark: true));
        Assert.Equal(AppTheme.Light, ThemeResolver.ResolveEffective(AppTheme.Light, systemIsDark: false));
    }

    [Fact]
    public void ResolveEffective_Dark_SiempreDevuelveDark()
    {
        Assert.Equal(AppTheme.Dark, ThemeResolver.ResolveEffective(AppTheme.Dark, systemIsDark: true));
        Assert.Equal(AppTheme.Dark, ThemeResolver.ResolveEffective(AppTheme.Dark, systemIsDark: false));
    }

    [Fact]
    public void ResolveEffective_System_SigueElTemaDeWindowsCuandoEsOscuro()
    {
        var result = ThemeResolver.ResolveEffective(AppTheme.System, systemIsDark: true);

        Assert.Equal(AppTheme.Dark, result);
    }

    [Fact]
    public void ResolveEffective_System_SigueElTemaDeWindowsCuandoEsClaro()
    {
        var result = ThemeResolver.ResolveEffective(AppTheme.System, systemIsDark: false);

        Assert.Equal(AppTheme.Light, result);
    }
}
