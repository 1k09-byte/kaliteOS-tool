using Microsoft.UI.Xaml.Data;
using System;

namespace stellarisKIT.Converters
{
    public sealed class InvertedBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool b ? !b : value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return value is bool b ? !b : value;
        }
    }
}
