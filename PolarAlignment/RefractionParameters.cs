using NINA.Equipment.Equipment.MyWeatherData;

namespace NINA.Plugins.PolarAlignment {
    public class RefractionParameters {

        public RefractionParameters(double pressureHPa, double temperature, double relativeHumidity, double wavelength = 0.55d) {            
            PressureHPa = pressureHPa;
            Temperature = temperature;
            RelativeHumidity = relativeHumidity;
            // Currently no source for this info. Taking standard value when not provided
            Wavelength = wavelength;
        }

        public double PressureHPa { get; }
        public double Temperature { get; }
        public double RelativeHumidity { get; }
        public double Wavelength { get; }

        public static RefractionParameters GetRefractionParameters(WeatherDataInfo info = null, double wavelength = 0.55d) {
            // https://en.wikipedia.org/wiki/Standard_temperature_and_pressure
            const double standardPressure = 1013.25;
            const double standardTemperature = 15;
            const double standardHumidity = 0;
                                
            if (info?.Connected == true) {
                var pressure = info.Pressure;
                if (double.IsNaN(pressure) || pressure < 500) {
                    pressure = standardPressure;
                }
                var temperature = info.Temperature;
                if (double.IsNaN(temperature) || temperature < -100 || temperature > 100) {
                    temperature = standardTemperature;
                }
                var humidity = info.Humidity;
                if (double.IsNaN(humidity)) {
                    humidity = standardHumidity;
                }
                return new RefractionParameters(pressure, temperature, humidity, wavelength);
            } else {
                return new RefractionParameters(standardPressure, standardTemperature, standardHumidity, wavelength);
            }
        }
    }
}
