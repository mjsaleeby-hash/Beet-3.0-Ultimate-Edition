using System.Globalization;
using System.Windows.Data;

namespace BeetsBackup.Helpers;

[ValueConversion(typeof(DateTime), typeof(string))]
public sealed class RelativeDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return string.Empty;
        var today = DateTime.Today;
        if (dt.Date == today) return $"Today, {dt:h:mm tt}";
        if (dt.Date == today.AddDays(-1)) return $"Yesterday, {dt:h:mm tt}";
        if (dt.Date == today.AddDays(1)) return $"Tomorrow, {dt:h:mm tt}";
        return dt.Year == today.Year
            ? $"{dt:MMM d, h:mm tt}"
            : $"{dt:MMM d yyyy, h:mm tt}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
