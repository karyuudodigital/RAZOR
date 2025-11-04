using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace SimpleLoadOrderOrganizer
{
    class GameIndexToImageConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int index)
            {
                // Allow the XAML binding to specify which image file to use (e.g., "top.png" or "list.png")
                string fileName = parameter as string ?? "top.png";

                string packUri = $"pack://application:,,,/assets/images/{index}/{fileName}";
                return new BitmapImage(new Uri(packUri, UriKind.Absolute));
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
