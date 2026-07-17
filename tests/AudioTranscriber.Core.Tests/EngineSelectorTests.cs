using System.Globalization;
using AudioTranscriber.Core.Transcription;
using Xunit;

namespace AudioTranscriber.Core.Tests;

public class EngineSelectorTests
{
    /// The real file a user brought in: a 1,634.7 MB mp4 — 65x the cloud cap.
    private const long RealBigFile = 1_714_366_054;

    [Fact]
    public void SmallFileOnCloud_KeepsCloud()
    {
        var decision = EngineSelector.Decide(5 * 1024 * 1024, isCloudEngine: true, isLocalModelAvailable: false);

        Assert.Equal(EngineDecision.Keep, decision);
        Assert.Null(EngineSelector.Notice(decision, 5 * 1024 * 1024));
    }

    [Fact]
    public void ExactlyAtTheCap_StillFitsInTheCloud()
    {
        var decision = EngineSelector.Decide(EngineSelector.CloudMaxBytes, isCloudEngine: true, isLocalModelAvailable: true);

        Assert.Equal(EngineDecision.Keep, decision);
    }

    [Fact]
    public void OneByteOverTheCap_DoesNotFit()
    {
        var decision = EngineSelector.Decide(EngineSelector.CloudMaxBytes + 1, isCloudEngine: true, isLocalModelAvailable: true);

        Assert.Equal(EngineDecision.SwitchToLocal, decision);
    }

    [Fact]
    public void BigFileOnLocalEngine_IsNeverTouched()
    {
        var decision = EngineSelector.Decide(RealBigFile, isCloudEngine: false, isLocalModelAvailable: true);

        Assert.Equal(EngineDecision.Keep, decision);
    }

    [Fact]
    public void BigFileOnCloud_WithModelReady_SwitchesToLocal()
    {
        var decision = EngineSelector.Decide(RealBigFile, isCloudEngine: true, isLocalModelAvailable: true);

        Assert.Equal(EngineDecision.SwitchToLocal, decision);
    }

    /// Never send someone to a ~1.5 GB model download as a side effect of pressing "Transcribir".
    [Fact]
    public void BigFileOnCloud_WithoutModel_AsksForTheModelInsteadOfSwitching()
    {
        var decision = EngineSelector.Decide(RealBigFile, isCloudEngine: true, isLocalModelAvailable: false);

        Assert.Equal(EngineDecision.NeedsLocalModel, decision);
    }

    [Fact]
    public void SwitchNotice_SaysWhatHappenedAndWhy_AndWarnsItIsSlower()
    {
        var notice = EngineSelector.Notice(EngineDecision.SwitchToLocal, RealBigFile);

        Assert.Contains("1,6 GB", notice);
        Assert.Contains("25 MB", notice);
        Assert.Contains("Local", notice);
        Assert.Contains("tardar", notice);
    }

    [Fact]
    public void NeedsModelNotice_TellsThemItIsAOneTimeDownload()
    {
        var notice = EngineSelector.Notice(EngineDecision.NeedsLocalModel, RealBigFile);

        Assert.Contains("modelo local", notice);
        Assert.Contains("una sola vez", notice);
    }

    /// Spanish decimal comma: the copy around the number is Spanish, so the number must match it.
    [Theory]
    [InlineData(1_714_366_054, "1,6 GB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    [InlineData(25L * 1024 * 1024, "25 MB")]
    public void FormatSize_IsReadable(long bytes, string expected)
    {
        Assert.Equal(expected, EngineSelector.FormatSize(bytes));
    }

    /// Must not depend on the machine's locale: an English Windows would otherwise render
    /// "1.6 GB" in the middle of an otherwise Spanish sentence.
    [Fact]
    public void FormatSize_IgnoresTheMachineCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("1,6 GB", EngineSelector.FormatSize(RealBigFile));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
