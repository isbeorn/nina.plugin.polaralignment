using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace NINA.Plugins.PolarAlignment.Converters {
    public class PointToCircleParameterConverter : IMultiValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            var converted = (Point)value;
            
            var parameters = parameter.ToString().Split('|');

            var coordinate = parameters[0];

            var radius = double.Parse(parameters[1], CultureInfo.InvariantCulture);

            switch(coordinate) {
                case "X1":
                    return converted.X - radius;
                case "Y1":
                    return converted.Y - radius;
                case "X2":
                    return radius*2;
                case "Y2":
                    return radius * 2;
            
            }
            
            return double.NaN;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            if(values.Length == 2) {
                if(values[0] is Point converted) {
                    if(values[1] is double pixelScale) {
                        var parameters = parameter.ToString().Split('|');

                        var coordinate = parameters[0];

                        // Prevent zero
                        pixelScale = Math.Max(0.001, pixelScale);

                        var radius = double.Parse(parameters[1], CultureInfo.InvariantCulture) / pixelScale;

                        switch (coordinate) {
                            case "X1":
                                return converted.X - radius;
                            case "Y1":
                                return converted.Y - radius;
                            case "X2":
                                return radius * 2;
                            case "Y2":
                                return radius * 2;

                        }
                    }                    
                }

            }
            return double.NaN;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }
}
