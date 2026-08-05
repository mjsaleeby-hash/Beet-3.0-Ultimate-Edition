using BeetsBackup.Models;
using FluentAssertions;
using System.Windows.Media;

namespace BeetsBackup.Tests.Models;

public class PieSliceTests
{
    /// <summary>
    /// Builds a slice with only the field under test varying. PieSlice uses `required`
    /// init-only properties, so every test needs the full set; this keeps that noise
    /// out of the test bodies.
    /// </summary>
    private static PieSlice Slice(
        double percentage,
        string name = "Documents",
        string sizeDisplay = "4.2 GB") =>
        new()
        {
            Name = name,
            Icon = "\U0001F4C1",
            SizeBytes = 4_509_715_660,
            SizeDisplay = sizeDisplay,
            Percentage = percentage,
            StartAngle = 0,
            SweepAngle = percentage / 100.0 * 360.0,
            FillColor = Colors.CornflowerBlue,
        };

    [Fact]
    [Trait("Category", "Unit")]
    public void IsNegligible_JustBelowThreshold_IsTrue()
    {
        Slice(0.049).IsNegligible.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsNegligible_ExactlyAtThreshold_IsFalse()
    {
        // The boundary is deliberately inclusive: exactly MinVisiblePercentage draws.
        Slice(PieSlice.MinVisiblePercentage).IsNegligible.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsNegligible_JustAboveThreshold_IsFalse()
    {
        Slice(0.051).IsNegligible.Should().BeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0.0)]
    [InlineData(0.049)]
    [InlineData(0.05)]
    [InlineData(0.051)]
    [InlineData(31.7)]
    [InlineData(100.0)]
    public void IsRenderable_IsAlwaysTheInverseOfIsNegligible(double percentage)
    {
        // THIS is the test that matters. The render guard and the legend's dimmed
        // styling drifted apart once already (0.028% vs 0.05%), which left legend rows
        // whose wedge was never drawn. Both now read MinVisiblePercentage, and this
        // pins them together so they cannot separate again.
        var slice = Slice(percentage);

        slice.IsRenderable.Should().Be(!slice.IsNegligible);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Announcement_CombinesNameSizeAndPercentage()
    {
        // One automation name per row. A screen reader reads this instead of the three
        // separate TextBlocks, which would otherwise be announced as disconnected
        // fragments with no relationship to each other.
        Slice(31.7, "Documents", "4.2 GB").Announcement
            .Should().Be("Documents, 4.2 GB, 31.7 percent");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Announcement_RoundsToOneDecimal_MatchingTheVisibleLabel()
    {
        // The legend renders {0:0.0}%. The announcement must agree with what is on
        // screen, or a sighted user and a screen-reader user hear different numbers.
        Slice(31.66, "Photos", "1.1 GB").Announcement
            .Should().Be("Photos, 1.1 GB, 31.7 percent");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Announcement_ForOtherSlice_ReadsNaturally()
    {
        Slice(2.4, "Other", "312 MB").Announcement
            .Should().Be("Other, 312 MB, 2.4 percent");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Announcement_ForNegligibleSlice_StillReportsItsSize()
    {
        // A negligible slice has no wedge but keeps its legend row, so it still needs a
        // usable announcement. "0.0 percent" is honest — the size carries the detail.
        Slice(0.004, "thumbs.db", "12 bytes").Announcement
            .Should().Be("thumbs.db, 12 bytes, 0.0 percent");
    }
}
