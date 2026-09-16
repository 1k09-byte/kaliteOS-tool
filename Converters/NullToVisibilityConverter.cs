using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace stellarisKIT.Converters
{
    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isNull = value == null || (value is string str && str.Length == 0);
            if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
                isNull = !isNull;
            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
