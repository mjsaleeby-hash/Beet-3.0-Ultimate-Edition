using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BeetsBackup.Helpers;

/// <summary>
/// Converts an enum value to <see cref="Visibility"/> — Visible when the value matches the parameter, Collapsed otherwise.
/// Used in XAML to conditionally show panels based on an enum-typed property.
/// </summary>
[ValueConversion(typeof(Enum), typeof(Visibility))]
public sealed class EnumVisConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.Equals(parameter) == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
