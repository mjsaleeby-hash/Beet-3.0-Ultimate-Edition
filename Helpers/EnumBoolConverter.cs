using System.Globalization;
using System.Windows.Data;

namespace BeetsBackup.Helpers;

/// <summary>
/// Converts an enum value to a boolean by comparing it to the converter parameter.
/// Used in XAML radio button groups bound to enum properties.
/// </summary>
[ValueConversion(typeof(Enum), typeof(bool))]
public sealed class EnumBoolConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.Equals(parameter) == true;
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (bool)value ? parameter : System.Windows.Data.Binding.DoNothing;
    }
}
