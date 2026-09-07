using Microsoft.UI.Xaml;
using Windows.Storage;

namespace WinIconFinder.Services;

public sealed class AppSettingsService
{
    private const string ThemePreferenceKey = "ThemePreference";

    private static ApplicationDataContainer Settings =>
        ApplicationData.Current.LocalSettings;

    public int ThemePreference
    {
        get => Settings.Values.TryGetValue(ThemePreferenceKey, out object? value) && value is int preference
               && preference is >= 0 and <= 2
            ? preference
            : 0;
        set => Settings.Values[ThemePreferenceKey] = value;
    }

    public static ElementTheme ToElementTheme(int preference) => preference switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default
    };
}
