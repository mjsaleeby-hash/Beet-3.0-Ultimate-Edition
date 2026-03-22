using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BeetsBackup.Helpers;

public class UsageToArcConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = value is double d ? d : 0;
        percent = Math.Clamp(percent, 0, 100);

        // Ring dimensions: 30x30 with 3px stroke → radius 12, center 15
        double radius = 12;
        double cx = 15, cy = 15;

        if (percent >= 100)
        {
            // Full circle — use an EllipseGeometry
            return new EllipseGeometry(new System.Windows.Point(cx, cy), radius, radius);
        }

        if (percent <= 0)
            return Geometry.Empty;

        double angle = percent / 100.0 * 360.0;
        double rad = (angle - 90) * Math.PI / 180.0;
        double endX = cx + radius * Math.Cos(rad);
        double endY = cy + radius * Math.Sin(rad);
        bool isLargeArc = angle > 180;

        var fig = new PathFigure
        {
            StartPoint = new System.Windows.Point(cx, cy - radius), // top center
            IsClosed = false
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = new System.Windows.Point(endX, endY),
            Size = new System.Windows.Size(radius, radius),
            IsLargeArc = isLargeArc,
            SweepDirection = SweepDirection.Clockwise
        });

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
