using System.Globalization;
using System.Windows.Data;

namespace BeetsBackup.Helpers;

public class EnumBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.Equals(parameter) == true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? parameter : System.Windows.Data.Binding.DoNothing;
    }
}
