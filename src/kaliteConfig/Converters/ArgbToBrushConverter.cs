using Windows.UI;
using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace kaliteConfig.Converters;

public sealed class ArgbToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int argb ? new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)((uint)argb >> 24),
            (byte)((uint)argb >> 16),
            (byte)((uint)argb >> 8),
            (byte)(uint)argb)) : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
