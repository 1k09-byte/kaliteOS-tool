using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace kaliteConfig.Services.Theming;

public static class ThemeHelper
{
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

    public static Brush PrimaryText()
        => Brush("TextFillColorPrimaryBrush", IsLight() ? Windows.UI.Color.FromArgb(255, 26, 26, 26) : Windows.UI.Color.FromArgb(255, 244, 244, 244));

    public static Brush SecondaryText()
        => Brush("TextFillColorSecondaryBrush", IsLight() ? Windows.UI.Color.FromArgb(255, 90, 90, 90) : Windows.UI.Color.FromArgb(255, 185, 185, 185));
}
