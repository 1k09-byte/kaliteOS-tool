using System;
using System.IO;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace kaliteConfig.Converters;

public sealed class ImageFileThumbnailConverter : IValueConverter
{
    public const int DecodeWidth = 480;

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || path.Length == 0 || !File.Exists(path))
            return null;

        return new BitmapImage
        {
            DecodePixelWidth = DecodeWidth,
            UriSource = new Uri(path),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
