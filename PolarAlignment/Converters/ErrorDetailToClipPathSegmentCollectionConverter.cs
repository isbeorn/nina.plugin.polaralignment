using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NINA.Plugins.PolarAlignment.Converters {
    public class ErrorDetailToClipPathSegmentCollectionConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            ErrorDetail detail = value as ErrorDetail;
            if(detail != null) {
                var collection = new PathSegmentCollection();
                
                var minX = Math.Min(detail.Origin.X, Math.Min(detail.Altitude.X, Math.Min(detail.Azimuth.X, detail.Total.X))) - 100;
                var minY = Math.Min(detail.Origin.Y, Math.Min(detail.Altitude.Y, Math.Min(detail.Azimuth.Y, detail.Total.Y))) - 100;
                var maxX = Math.Max(detail.Origin.X, Math.Max(detail.Altitude.X, Math.Max(detail.Azimuth.X, detail.Total.X))) + 100;
                var maxY = Math.Max(detail.Origin.Y, Math.Max(detail.Altitude.Y, Math.Max(detail.Azimuth.Y, detail.Total.Y))) + 100;


                collection.Add(new LineSegment(new Point(minX, maxY ), false));
                collection.Add(new LineSegment(new Point(maxX ,maxY), false));
                collection.Add(new LineSegment(new Point(maxX, minY), false));
                collection.Add(new LineSegment(new Point(minX, minY), false));
                collection.Freeze();


                var figure = new PathFigure(new Point(minX, minY), collection, true);
                var pathFigureCollection = new PathFigureCollection(new List<PathFigure> { figure });
                return pathFigureCollection;

            } 
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }
}
