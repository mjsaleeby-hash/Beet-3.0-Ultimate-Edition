using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BeetsBackup.Helpers;

/// <summary>
/// Converts a drive usage percentage (0-100) into a <see cref="Geometry"/> arc
/// for the disk usage ring indicator in the drive selector.
/// </summary>
/// <remarks>
/// Ring dimensions are hard-coded: 30x30 viewport, 3px stroke, radius 12, center (15, 15).
/// At 100% the arc becomes a full <see cref="EllipseGeometry"/>; at 0% returns <see cref="Geometry.Empty"/>.
/// </remarks>
[ValueConversion(typeof(double), typeof(Geometry))]
public sealed class UsageToArcConverter : IValueConverter
{
    // Ring layout constants matching the XAML viewport
    private const double Radius = 12;
    private const double CenterX = 15;
    private const double CenterY = 15;

    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = value is double d ? d : 0;
        percent = Math.Clamp(percent, 0, 100);

        if (percent >= 100)
            return new EllipseGeometry(new System.Windows.Point(CenterX, CenterY), Radius, Radius);

        if (percent <= 0)
            return Geometry.Empty;

        // Convert percentage to angle in radians (0 degrees = 12 o'clock)
        double angle = percent / 100.0 * 360.0;
        double rad = (angle - 90) * Math.PI / 180.0;
        double endX = CenterX + Radius * Math.Cos(rad);
        double endY = CenterY + Radius * Math.Sin(rad);
        bool isLargeArc = angle > 180;

        var fig = new PathFigure
        {
            StartPoint = new System.Windows.Point(CenterX, CenterY - Radius),
            IsClosed = false
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = new System.Windows.Point(endX, endY),
            Size = new System.Windows.Size(Radius, Radius),
            IsLargeArc = isLargeArc,
            SweepDirection = SweepDirection.Clockwise
        });

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
