using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace kaliteConfig.Converters
{
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
