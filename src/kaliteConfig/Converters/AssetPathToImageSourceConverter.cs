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
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace kaliteConfig.Converters
{
    /// <summary>
    /// WinUI's Image/BitmapImage cannot decode SVG (helium/riot/discord/whatsapp
    /// logos ship as .svg and rendered blank). Route .svg through SvgImageSource,
    /// everything else through BitmapImage.
    /// </summary>
    public sealed class AssetPathToImageSourceConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    return new SvgImageSource(new Uri(path));
                return new BitmapImage(new Uri(path));
            }
            catch { return null; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
