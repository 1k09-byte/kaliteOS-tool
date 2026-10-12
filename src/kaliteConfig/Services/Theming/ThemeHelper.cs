// Central theme queries for C# code that cannot use {ThemeResource}.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace kaliteConfig.Services.Theming;

// One place for light-or-dark questions from code-behind and view models.
public static class ThemeHelper
{
    // True while the app resolves the Light theme dictionary.
    public static bool IsLight()
    {
        try
        {
            var root = App.MainWindow?.Content as FrameworkElement;
            if (root is not null) return root.ActualTheme == ElementTheme.Light;
            return Application.Current?.RequestedTheme == ApplicationTheme.Light;
        }
        catch { return false; }
    }

    // Theme brush by key, with a fixed fallback that never throws.
    public static Brush Brush(string key, Windows.UI.Color fallback)
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res is not null && res.TryGetValue(key, out var b) && b is Brush brush) return brush;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }

    // Theme-AWARE brush lookup for code that cannot use {ThemeResource}.
    //
    // Plain Application.Current.Resources.TryGetValue is NOT theme-aware: it
    // serves the Default-dictionary entry (dark, in this app) no matter which
    // theme is active. And Application.Current.Resources.ThemeDictionaries is
    // empty - the Light/Dark dictionaries live inside the MERGED theme
    // dictionaries (KaliteTheme.xaml), not on the root. So this searches the
    // merged dictionaries last-to-first (last merged wins, matching XAML
    // lookup order) for the active theme's instance. That instance is the same
    // one live elements resolve, so mutating it re-skins the screen.
    public static SolidColorBrush? ThemedBrush(string key, bool light)
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res is null) return null;
            string theme = light ? "Light" : "Dark";
            for (int i = res.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var merged = res.MergedDictionaries[i];
                if (merged.ThemeDictionaries.TryGetValue(theme, out var entry) &&
                    entry is ResourceDictionary themed &&
                    themed.TryGetValue(key, out var value) &&
                    value is SolidColorBrush brush)
                    return brush;
            }
            if (res.TryGetValue(key, out var fallback) && fallback is SolidColorBrush fb)
                return fb;
        }
        catch { }
        return null;
    }

    // Primary text for the active theme.
    public static Brush PrimaryText()
        => Brush("TextFillColorPrimaryBrush", IsLight() ? Windows.UI.Color.FromArgb(255, 26, 26, 26) : Windows.UI.Color.FromArgb(255, 244, 244, 244));

    // Secondary text for the active theme.
    public static Brush SecondaryText()
        => Brush("TextFillColorSecondaryBrush", IsLight() ? Windows.UI.Color.FromArgb(255, 90, 90, 90) : Windows.UI.Color.FromArgb(255, 185, 185, 185));
}
