using Windows.Storage;

namespace WinIconFinder.Services;

/// <summary>
/// Tracks one-time informational tips so each is shown only on its first
/// occurrence. Backed by roaming-free local app settings.
/// </summary>
public sealed class UserTipsService
{
    private const string FontIconTipKey = "TipShown_FontIconShipping";

    private static ApplicationDataContainer Settings =>
        ApplicationData.Current.LocalSettings;

    /// <summary>
    /// True the first time the user copies a XAML FontIcon snippet; false
    /// afterwards. Marks the tip as seen as a side effect.
    /// </summary>
    public bool ShouldShowFontIconTip()
    {
        if (Settings.Values.ContainsKey(FontIconTipKey))
        {
            return false;
        }

        Settings.Values[FontIconTipKey] = true;
        return true;
    }
}
