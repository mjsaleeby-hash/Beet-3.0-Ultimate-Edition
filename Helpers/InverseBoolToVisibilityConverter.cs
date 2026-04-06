using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BeetsBackup.Helpers;

/// <summary>
/// Inverts a boolean for visibility: <c>true</c> becomes <see cref="Visibility.Collapsed"/>
/// and <c>false</c> becomes <see cref="Visibility.Visible"/>.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
