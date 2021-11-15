using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace NINA.Plugins.PolarAlignment.Converters {
    class AddToDoubleConverter : IValueConverter {

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            var val = System.Convert.ToDouble(value);
            var increment = double.Parse(parameter.ToString(), CultureInfo.InvariantCulture);
            return val + increment;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }
}
