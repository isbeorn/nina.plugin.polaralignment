using NINA.Equipment.Equipment.MyWeatherData;

namespace NINA.Plugins.PolarAlignment {
    public class RefrectionParameters {
        public RefrectionParameters(double elevation, double pressureHPa, double temperature, double relativeHumidity) {
            Elevation = elevation;
            PressureHPa = pressureHPa;
            Temperature = temperature;
            RelativeHumidity = relativeHumidity;
            // Currently no source for this info. Taking standard value
            Wavelength = 0.55d;
        }

        public double Elevation { get; }
        public double PressureHPa { get; }

        public double Temperature { get; }
        public double RelativeHumidity { get; }
        public double Wavelength { get; }

        public static RefrectionParameters GetRefrectionParameters(double elevation, WeatherDataInfo info = null) {
            // https://en.wikipedia.org/wiki/Standard_temperature_and_pressure
            const double standardPressure = 1013.25;
            const double standardTemperature = 15;
            const double standardHumidity = 0;
                                
            if (info?.Connected == true) {
                var pressure = info.Pressure;
                if (double.IsNaN(pressure)) {
                    pressure = standardPressure;
                }
                var temperature = info.Temperature;
                if (double.IsNaN(temperature)) {
                    temperature = standardTemperature;
                }
                var humidity = info.Humidity;
                if (double.IsNaN(humidity)) {
                    humidity = standardHumidity;
                }
                return new RefrectionParameters(elevation, pressure, temperature, humidity);
            } else {
                return new RefrectionParameters(elevation, standardPressure, standardTemperature, standardHumidity);
            }
        }
    }
}
