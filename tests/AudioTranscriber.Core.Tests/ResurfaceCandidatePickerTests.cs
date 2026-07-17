using AudioTranscriber.Core.Notes;

namespace AudioTranscriber.Core.Tests;

public class ResurfaceCandidatePickerTests
{
    private static readonly DateTime Now = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    // ---- IsEligible ---------------------------------------------------------------------------

    [Fact]
    public void IsEligible_NotaDeHoy_NoEsElegible()
    {
        Assert.False(ResurfaceCandidatePicker.IsEligible(Now, Now));
    }

    [Fact]
    public void IsEligible_NotaDeExactamente14Dias_EsElegible()
    {
        var createdAt = Now.AddDays(-14);
        Assert.True(ResurfaceCandidatePicker.IsEligible(createdAt, Now));
    }

    [Fact]
    public void IsEligible_NotaDe13Dias_NoEsElegible()
    {
        var createdAt = Now.AddDays(-13);
        Assert.False(ResurfaceCandidatePicker.IsEligible(createdAt, Now));
    }

    [Fact]
    public void IsEligible_NotaDe30Dias_EsElegible()
    {
        Assert.True(ResurfaceCandidatePicker.IsEligible(Now.AddDays(-30), Now));
    }

    // ---- PickCandidate --------------------------------------------------------------------------

    [Fact]
    public void PickCandidate_EligeLaMasVieja()
    {
        var candidates = new[]
        {
            new ResurfaceCandidate("n1", "Nota 1", Now.AddDays(-20)),
            new ResurfaceCandidate("n2", "Nota 2", Now.AddDays(-40)),
            new ResurfaceCandidate("n3", "Nota 3", Now.AddDays(-15)),
        };

        var picked = ResurfaceCandidatePicker.PickCandidate(candidates, new HashSet<string>());

        Assert.NotNull(picked);
        Assert.Equal("n2", picked!.Id);
    }

    [Fact]
    public void PickCandidate_ExcluyeDescartadas()
    {
        var candidates = new[]
        {
            new ResurfaceCandidate("n1", "Nota 1", Now.AddDays(-20)),
            new ResurfaceCandidate("n2", "Nota 2", Now.AddDays(-40)),
        };

        var picked = ResurfaceCandidatePicker.PickCandidate(candidates, new HashSet<string> { "n2" });

        Assert.NotNull(picked);
        Assert.Equal("n1", picked!.Id);
    }

    [Fact]
    public void PickCandidate_SinCandidatas_DevuelveNull()
    {
        Assert.Null(ResurfaceCandidatePicker.PickCandidate(Array.Empty<ResurfaceCandidate>(), new HashSet<string>()));
    }

    [Fact]
    public void PickCandidate_TodasDescartadas_DevuelveNull()
    {
        var candidates = new[] { new ResurfaceCandidate("n1", "Nota 1", Now.AddDays(-20)) };

        Assert.Null(ResurfaceCandidatePicker.PickCandidate(candidates, new HashSet<string> { "n1" }));
    }

    // ---- FormatRelativeTime ---------------------------------------------------------------------

    [Theory]
    [InlineData(0, "hoy")]
    [InlineData(1, "hace 1 día")]
    [InlineData(3, "hace 3 días")]
    [InlineData(7, "hace 1 semana")]
    [InlineData(14, "hace 2 semanas")]
    [InlineData(30, "hace 1 mes")]
    [InlineData(90, "hace 3 meses")]
    [InlineData(365, "hace 1 año")]
    [InlineData(730, "hace 2 años")]
    public void FormatRelativeTime_DevuelveTextoEsperado(int daysAgo, string expected)
    {
        Assert.Equal(expected, ResurfaceCandidatePicker.FormatRelativeTime(Now.AddDays(-daysAgo), Now));
    }

    [Fact]
    public void FormatRelativeTime_RelojDesincronizado_ClampeaACero()
    {
        // createdAt en el "futuro" respecto de now -- no debería dar un texto negativo.
        Assert.Equal("hoy", ResurfaceCandidatePicker.FormatRelativeTime(Now.AddDays(5), Now));
    }
}
