using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BeetsBackup.Helpers;

/// <summary>
/// Converts a progress percentage (0-100) into a <see cref="Geometry"/> arc
/// for the large circular progress ring shown in the transfer progress dialog.
/// </summary>
/// <remarks>
/// Ring dimensions match the dialog's XAML viewport: 200x200, 14px stroke, radius 86, center (100, 100).
/// At 100% the arc becomes a full <see cref="EllipseGeometry"/>; at 0% returns <see cref="Geometry.Empty"/>.
/// </remarks>
[ValueConversion(typeof(double), typeof(Geometry))]
public sealed class ProgressToArcConverter : IValueConverter
{
    private const double Radius = 86;
    private const double CenterX = 100;
    private const double CenterY = 100;

    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = value switch
        {
            double d => d,
            int i => i,
            _ => 0
        };
        percent = Math.Clamp(percent, 0, 100);

        if (percent >= 100)
            return new EllipseGeometry(new System.Windows.Point(CenterX, CenterY), Radius, Radius);

        if (percent <= 0)
            return Geometry.Empty;

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
