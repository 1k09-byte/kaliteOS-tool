using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace stellarisKIT.Converters
{
    public sealed class InvertedBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return (value is Visibility v && v == Visibility.Visible) ? false : true;
        }
    }
}
