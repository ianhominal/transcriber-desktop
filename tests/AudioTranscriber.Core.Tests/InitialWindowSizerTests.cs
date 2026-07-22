using AudioTranscriber.Core.Ui;

namespace AudioTranscriber.Core.Tests;

public class InitialWindowSizerTests
{
    private const double MinWidth = 880;
    private const double MinHeight = 600;

    [Fact]
    public void Compute_usa_el_80_por_ciento_del_work_area_en_un_monitor_full_hd()
    {
        var workArea = new ScreenRect(0, 0, 1920, 1080);

        var result = InitialWindowSizer.Compute(workArea, MinWidth, MinHeight);

        Assert.Equal(1536, result.Width); // 1920 * 0.8
        Assert.Equal(864, result.Height); // 1080 * 0.8
    }

    [Fact]
    public void Compute_centra_el_rect_dentro_del_work_area()
    {
        var workArea = new ScreenRect(100, 50, 1920, 1080); // work area que no arranca en (0,0)

        var result = InitialWindowSizer.Compute(workArea, MinWidth, MinHeight);

        Assert.Equal(workArea.X + (workArea.Width - result.Width) / 2, result.X);
        Assert.Equal(workArea.Y + (workArea.Height - result.Height) / 2, result.Y);
    }

    [Fact]
    public void Compute_nunca_baja_del_minimo_en_un_monitor_chico_de_laptop()
    {
        // 80% de un work area de laptop chica (1024x768) da menos que MinWidth/MinHeight.
        var workArea = new ScreenRect(0, 0, 1024, 768);

        var result = InitialWindowSizer.Compute(workArea, MinWidth, MinHeight);

        Assert.Equal(MinWidth, result.Width);
        Assert.True(result.Height >= MinHeight);
    }

    [Fact]
    public void Compute_no_supera_el_maximo_en_un_monitor_4K()
    {
        var workArea = new ScreenRect(0, 0, 3840, 2160);

        var result = InitialWindowSizer.Compute(workArea, MinWidth, MinHeight, maxWidth: 1600, maxHeight: 1000);

        Assert.Equal(1600, result.Width);
        Assert.Equal(1000, result.Height);
    }

    [Fact]
    public void Compute_respeta_un_maximo_custom()
    {
        var workArea = new ScreenRect(0, 0, 3840, 2160);

        var result = InitialWindowSizer.Compute(workArea, MinWidth, MinHeight, maxWidth: 1400, maxHeight: 900);

        Assert.Equal(1400, result.Width);
        Assert.Equal(900, result.Height);
    }
}
