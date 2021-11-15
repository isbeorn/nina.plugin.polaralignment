using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace NINA.Plugins.PolarAlignment.Converters {
    public class ClipToScaleConverter : IMultiValueConverter {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            var dimension = (double)values[0];
            var bounds = (Rect)values[1];            

            return Math.Min(dimension / (bounds.Right - bounds.Left), dimension / (bounds.Bottom - bounds.Top));
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }
}
