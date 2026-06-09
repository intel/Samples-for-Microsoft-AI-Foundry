using System;
using Microsoft.UI.Xaml.Data;

namespace TechnicianAssistant.Converters
{
    public class RecordingIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isRecording)
            {
                return isRecording ? "\uE71A" : "\uE720";
            }
            return "\uE720";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
