using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace kaliteConfig.Converters
{
    public sealed class InvertedBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // SelectedScheme is null while the scheme list is still loading, so
            // IsEnabled bindings on the details panel evaluate with null here.
            // Returning null for a bool target crashed the page (interop cast
            // exception) the moment the user navigated to it. Treat null as
            // disabled-by-inversion (true) so the page loads cleanly and the
            // buttons enable once a scheme is selected.
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
