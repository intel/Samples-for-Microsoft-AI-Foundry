using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace TechnicianAssistant.Converters
{
    public class RecordingIconColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isRecording && isRecording)
            {
                // Red for stop/recording state
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 50, 50));
            }
            // Teal/cyan for microphone ready state
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 180, 160));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
