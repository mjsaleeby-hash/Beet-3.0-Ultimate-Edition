using Application = System.Windows.Application;
using ResourceDictionary = System.Windows.ResourceDictionary;

namespace BeetsBackup.Services;

public class ThemeService
{
    private readonly SettingsService _settings;

    public bool IsDark => _settings.IsDarkMode;

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
    }

    public void ApplySaved()
    {
        SetTheme(_settings.IsDarkMode);
    }

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

    public void Toggle() => SetTheme(!_settings.IsDarkMode);
}
