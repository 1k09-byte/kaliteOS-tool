// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace kaliteConfig.Converters;

/// <summary>
/// Swaps a card's background for the muted one when the bound flag is true.
///
/// The "Hidden" settings section uses this so settings nobody should be reaching for
/// by default read as a set apart, without a different card style having to be
/// threaded through the template. Returning the normal brush when false is what
/// keeps every other section unchanged.
/// </summary>
public sealed class MutedBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool muted = value is bool b && b;
        string key = muted ? "LayerFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush";
        object? resource = null;
        if (Application.Current?.Resources.TryGetValue(key, out resource) == true
            && resource is Brush brush)
        {
            return brush;
        }
        return muted
            ? new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.08 }
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
