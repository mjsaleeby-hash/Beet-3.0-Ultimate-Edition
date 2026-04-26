using System.Globalization;
using System.Windows.Data;

namespace BeetsBackup.Helpers;

public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values[0] is double trackWidth && values[1] is double percent)
            return Math.Max(0, trackWidth * (percent / 100.0));
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
