// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace kaliteConfig.Converters;

/// <summary>
/// bool → <see cref="GridLength"/>. Lets an optional table column collapse to
/// zero width so a hidden column leaves no gap behind. The converter parameter
/// is the width used when true (default 100).
///
/// Bound to <see cref="GridLength"/> column widths, the same width must be used
/// for the header and the row cells: with Auto widths the header's long caption
/// sizes its column differently from the row's short number and the two drift
/// out of alignment.
/// </summary>
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool on = value is bool b && b;

        double width = 100;
        if (parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            width = parsed;
        }

        return on ? new GridLength(width) : new GridLength(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is GridLength gl && gl.Value > 0;
}