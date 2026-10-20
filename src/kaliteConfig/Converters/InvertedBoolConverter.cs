using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace kaliteConfig.Converters
{
    public sealed class InvertedBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is null) return true;
            return value is bool b ? !b : value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is null) return DependencyProperty.UnsetValue;
            return value is bool b ? !b : value;
        }
    }
}
