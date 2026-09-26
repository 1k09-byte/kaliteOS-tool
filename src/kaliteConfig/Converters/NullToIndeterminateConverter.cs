// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace kaliteConfig.Converters;

/// <summary>
/// Null → indeterminate ProgressBar (download percent unknown / resolving),
/// non-null → determinate at the bound percent. Maps double? → bool for
/// ProgressBar.IsIndeterminate.
/// </summary>
public sealed class NullToIndeterminateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is null;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
