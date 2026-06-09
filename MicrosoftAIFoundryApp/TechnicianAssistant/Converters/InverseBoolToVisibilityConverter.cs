using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace TechnicianAssistant.Converters
{
    /// <summary>
    /// Returns <see cref="Visibility.Visible"/> when the bound <see langword="bool"/> is
    /// <see langword="false"/>, and <see cref="Visibility.Collapsed"/> when it is
    /// <see langword="true"/>. Used to show placeholder panels when no data is present.
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            value is Visibility.Collapsed;
    }
}
