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
}
