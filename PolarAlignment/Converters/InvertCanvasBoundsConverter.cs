using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace NINA.Plugins.PolarAlignment.Converters {
    public class InvertCanvasBoundsConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            var rect = (Rect)value;
            var direction = parameter.ToString();
            switch(direction.ToLower()) {
                case "left":
                    return -rect.Left;
                case "top":
                    return -rect.Top;
                case "width":
                    return -rect.Width;
                case "height":
                    return -rect.Height;
                case "bottom":
                    return -rect.Bottom;
                case "right":
                    return -rect.Right;
            }
            return double.NaN;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }
}
