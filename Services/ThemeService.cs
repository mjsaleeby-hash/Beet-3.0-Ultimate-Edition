using Application = System.Windows.Application;
using ResourceDictionary = System.Windows.ResourceDictionary;

namespace BeetsBackup.Services;

/// <summary>
/// Manages dark/light theme switching by swapping WPF resource dictionaries
/// and persisting the user's preference via <see cref="SettingsService"/>.
/// </summary>
public sealed class ThemeService
{
    private readonly SettingsService _settings;

    /// <summary>Whether the current theme is dark mode.</summary>
    public bool IsDark => _settings.IsDarkMode;

    /// <summary>
    /// Initializes the theme service with a reference to the settings store.
    /// </summary>
    /// <param name="settings">The settings service used to persist theme preference.</param>
    public ThemeService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Applies the saved theme preference on application startup.
    /// </summary>
    public void ApplySaved()
    {
        SetTheme(_settings.IsDarkMode);
    }

    /// <summary>
    /// Switches to the specified theme, persists the choice, and updates the active resource dictionary.
    /// </summary>
    /// <param name="dark">True for dark mode, false for light mode.</param>
    public void SetTheme(bool dark)
    {
        _settings.IsDarkMode = dark;
        _settings.Save();

        var dict = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(dict);
    }

    /// <summary>
    /// Toggles between dark and light mode.
    /// </summary>
    public void Toggle() => SetTheme(!_settings.IsDarkMode);
}
