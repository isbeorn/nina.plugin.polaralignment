using FluentAssertions;
using NINA.Astrometry;
using NINA.Core.Utility;

namespace NINA.Plugins.PolarAlignment.Test {
    public class PolarErrorDeterminationTest {
        [SetUp]
        public void Setup() {
        }
        
        [Test]
        public void PolarErrorDetermination_InitialMountError_SomeDeclinationError_ErrorIsEquals_ForBothPointDirections() {
            var latitude = Angle.ByDegree(49);
            var longitude = Angle.ByDegree(7);
            var elevation = 250d;
            var time = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var refraction = RefractionParameters.GetRefractionParameters(new Equipment.Equipment.MyWeatherData.WeatherDataInfo() { Connected = true, Pressure = 0, Temperature = 0.0001, Humidity = 20 });

            var solve1 = new Coordinates(Angle.ByDegree(20), Angle.ByDegree(40), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position1 = new Position(solve1, 0, latitude, longitude, elevation, refraction);

            var solve2 = new Coordinates(Angle.ByDegree(60), Angle.ByDegree(41), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position2 = new Position(solve2, 0, latitude, longitude, elevation, refraction);

            var solve3 = new Coordinates(Angle.ByDegree(90), Angle.ByDegree(42), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position3 = new Position(solve3, 0, latitude, longitude, elevation, refraction);
            

            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() {  Coordinates = solve3 }, position1, position2, position3, latitude, longitude, elevation, refraction, true);

            var error2 = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = solve1 }, position3, position2, position1, latitude, longitude, elevation, refraction, true);

            error.InitialMountAxisTotalError.Degree.Should().NotBeApproximately(0, 0001);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(error2.InitialMountAxisAltitudeError.Degree, 1.0 / 3600.0);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(error2.InitialMountAxisAzimuthError.Degree, 1.0 / 3600.0);
            error.InitialMountAxisTotalError.Degree.Should().BeApproximately(error2.InitialMountAxisTotalError.Degree, 1.0 / 3600.0);
        }


        [Test]
        public void PolarErrorDetermination_InitialMountError_NoDeclinationError_ErrorIsZero() {
            var latitude = Angle.ByDegree(49);
            var longitude = Angle.ByDegree(7);
            var elevation = 250d;
            var time = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            RefractionParameters refraction = new RefractionParameters(0, 0.0001, 0, 0);

            var solve1 = new Coordinates(Angle.ByDegree(20), Angle.ByDegree(40), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position1 = new Position(solve1, 0,latitude, longitude, elevation, refraction);

            var solve2 = new Coordinates(Angle.ByDegree(60), Angle.ByDegree(40), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position2 = new Position(solve2, 0, latitude, longitude, elevation, refraction);

            var solve3 = new Coordinates(Angle.ByDegree(90), Angle.ByDegree(40), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position3 = new Position(solve3, 0, latitude, longitude, elevation, refraction);


            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = solve1 }, position3, position2, position1, latitude, longitude, elevation, refraction, true);

            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(0, 1.0 / 3600.0);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(0, 1.0 / 3600.0);
            error.InitialMountAxisTotalError.Degree.Should().BeApproximately(0, 1.0 / 3600.0);
        }


        class CustomTime : ICustomDateTime {
            DateTime time;
            public CustomTime(DateTime time) {
                this.time = time;
            }

            public DateTime Now => time;

            public DateTime UtcNow => time;
        }

            [Test]
        public void PolarErrorDetermination_VeryDifferentAltitudePoints_TruePole() {
            var latitude = Angle.ByDegree(40);
            var longitude = Angle.ByDegree(0);
            var elevation = 250;
            var time1 = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var time2 = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var time3 = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var refraction = RefractionParameters.GetRefractionParameters(new Equipment.Equipment.MyWeatherData.WeatherDataInfo() { Connected = true, Pressure = 1005, Temperature = 7, Humidity = 0.8 }, 0.574);
            // prepare a misaligned mount polar axis 
            var polarAxis = new TopocentricCoordinates(Angle.ByDegree(1), latitude + Angle.ByDegree(1), latitude, longitude, time1);
            // and a first alt-az position for the mount
            var f1 = new TopocentricCoordinates(Angle.ByDegree(70), Angle.ByDegree(20), latitude, longitude, time1);
            var v1 = Vector3.CoordinatesToUnitVector(f1);
            var p = Vector3.CoordinatesToUnitVector(polarAxis);
            // convert into unit-norm vectors, and rotate by Rodrigues around the mount polar axis, 30 deg to the West
            var v2 = Vector3.RotateByRodrigues(v1, p, Angle.ByDegree(-30));
            var v3 = Vector3.RotateByRodrigues(v2, p, Angle.ByDegree(-30));
            var f2 = v2.ToTopocentric(latitude, longitude, elevation, time2);
            var f3 = v3.ToTopocentric(latitude, longitude, elevation, time3);
            // transform the three alt-az position into equatorial coordinates, accounting for refraction,
            // these three equatorial positions simulate the three plate-solved fields
            var s1 = f1.Transform(Epoch.J2000, refraction.PressureHPa, refraction.Temperature, refraction.RelativeHumidity, refraction.Wavelength);
            var s2 = f2.Transform(Epoch.J2000, refraction.PressureHPa, refraction.Temperature, refraction.RelativeHumidity, refraction.Wavelength);
            var s3 = f3.Transform(Epoch.J2000, refraction.PressureHPa, refraction.Temperature, refraction.RelativeHumidity, refraction.Wavelength);
            // verify that, when neglecting refraction, the three alt-az positions corresponding to the
            // three simulated fields would be lower in altitude than the initial ones
            var q1 = s1.Transform(latitude, longitude, s1.DateTime.Now);
            var q2 = s2.Transform(latitude, longitude, s2.DateTime.Now);
            var q3 = s3.Transform(latitude, longitude, s3.DateTime.Now);
            q1.Altitude.Degree.Should().BeLessThan(f1.Altitude.Degree);
            q2.Altitude.Degree.Should().BeLessThan(f2.Altitude.Degree);
            q3.Altitude.Degree.Should().BeLessThan(f3.Altitude.Degree);
            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = s1 }, 
                new Position(s3, 0, latitude, longitude, elevation, refraction), 
                new Position(s2, 0, latitude, longitude, elevation, refraction), 
                new Position(s1, 0, latitude, longitude, elevation, refraction), latitude, longitude, elevation, refraction, true);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(1.0, 1.0 / 3600.0);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(1.0, 1.0 / 3600.0);
        }

        [Test]
        public void PolarErrorDetermination_VeryDifferentAltitudePoints_RefractedPole() {
            var latitude = Angle.ByDegree(40);
            var longitude = Angle.ByDegree(0);
            var elevation = 250;
            var time1 = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var time2 = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var time3 = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var refraction = RefractionParameters.GetRefractionParameters(new Equipment.Equipment.MyWeatherData.WeatherDataInfo() { Connected = true, Pressure = 1005, Temperature = 7, Humidity = 0.8 }, 0.574);
            // prepare a misaligned mount polar axis 
            var polarAxis = new TopocentricCoordinates(Angle.ByDegree(1), latitude + Angle.ByDegree(1), latitude, longitude, time1);
            // and a first alt-az position for the mount
            var f1 = new TopocentricCoordinates(Angle.ByDegree(70), Angle.ByDegree(20), latitude, longitude, time1);
            var v1 = Vector3.CoordinatesToUnitVector(f1);
            var p = Vector3.CoordinatesToUnitVector(polarAxis);
            // convert into unit-norm vectors, and rotate by Rodrigues around the mount polar axis, 30 deg to the West
            var v2 = Vector3.RotateByRodrigues(v1, p, Angle.ByDegree(-30));
            var v3 = Vector3.RotateByRodrigues(v2, p, Angle.ByDegree(-30));
            var f2 = v2.ToTopocentric(latitude, longitude, elevation, time2);
            var f3 = v3.ToTopocentric(latitude, longitude, elevation, time3);
            // transform the three alt-az position into equatorial coordinates, accounting for refraction,
            // these three equatorial positions simulate the three plate-solved fields
            var s1 = f1.Transform(Epoch.J2000, refraction.PressureHPa, refraction.Temperature, refraction.RelativeHumidity, refraction.Wavelength);
            var s2 = f2.Transform(Epoch.J2000, refraction.PressureHPa, refraction.Temperature, refraction.RelativeHumidity, refraction.Wavelength);
            var s3 = f3.Transform(Epoch.J2000, refraction.PressureHPa, refraction.Temperature, refraction.RelativeHumidity, refraction.Wavelength);
            // verify that, when neglecting refraction, the three alt-az positions corresponding to the
            // three simulated fields would be lower in altitude than the initial ones
            var q1 = s1.Transform(latitude, longitude, s1.DateTime.Now);
            var q2 = s2.Transform(latitude, longitude, s2.DateTime.Now);
            var q3 = s3.Transform(latitude, longitude, s3.DateTime.Now);
            q1.Altitude.Degree.Should().BeLessThan(f1.Altitude.Degree);
            q2.Altitude.Degree.Should().BeLessThan(f2.Altitude.Degree);
            q3.Altitude.Degree.Should().BeLessThan(f3.Altitude.Degree);
            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = s1 },
                new Position(s3, 0, latitude, longitude, elevation, refraction),
                new Position(s2, 0, latitude, longitude, elevation, refraction),
                new Position(s1, 0, latitude, longitude, elevation, refraction), latitude, longitude, elevation, refraction, false);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(1.0 - 69.3/3600.0, 1.0 / 3600.0);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(1.0, 1.0 / 3600.0);
        }


        [Test]
        public void PolarErrorDetermination_VeryDifferentAltitudePoints_FromAstropy_TruePole() {
            var latitude = Angle.ByDegree(40);
            var longitude = Angle.ByDegree(0);
            var elevation = 250;
            var time = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            // values calculated with Astropy and quaternion rotations
            var s1 = new Coordinates(Angle.ByDegree(186.4193401), Angle.ByDegree(27.75369312), Epoch.J2000, time);
            var s2 = new Coordinates(Angle.ByDegree(156.6798968), Angle.ByDegree(27.40124463), Epoch.J2000, time);
            var s3 = new Coordinates(Angle.ByDegree(127.00972423), Angle.ByDegree(27.34989335), Epoch.J2000, time);
            var refraction = RefractionParameters.GetRefractionParameters(new Equipment.Equipment.MyWeatherData.WeatherDataInfo() { Connected = true, Pressure = 1005, Temperature = 7, Humidity = 0.8 }, 0.574);
            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = s1 },
                new Position(s3, 0, latitude, longitude, elevation, refraction),
                new Position(s2, 0, latitude, longitude, elevation, refraction),
                new Position(s1, 0, latitude, longitude, elevation, refraction), latitude, longitude, elevation, refraction, true);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(1.0, 1.0/3600.0);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(1.0, 1.0/3600.0);
        }

        [Test]
        public void PolarErrorDetermination_VeryDifferentAltitudePoints_FromAstropy_RefractedPole() {
            var latitude = Angle.ByDegree(40);
            var longitude = Angle.ByDegree(0);
            var elevation = 250;
            var time = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            // values calculated with Astropy and quaternion rotations
            var s1 = new Coordinates(Angle.ByDegree(186.4193401), Angle.ByDegree(27.75369312), Epoch.J2000, time);
            var s2 = new Coordinates(Angle.ByDegree(156.6798968), Angle.ByDegree(27.40124463), Epoch.J2000, time);
            var s3 = new Coordinates(Angle.ByDegree(127.00972423), Angle.ByDegree(27.34989335), Epoch.J2000, time);
            var refraction = RefractionParameters.GetRefractionParameters(new Equipment.Equipment.MyWeatherData.WeatherDataInfo() { Connected = true, Pressure = 1005, Temperature = 7, Humidity = 0.8 }, 0.574);
            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = s1 },
                new Position(s3, 0, latitude, longitude, elevation, refraction),
                new Position(s2, 0, latitude, longitude, elevation, refraction),
                new Position(s1, 0, latitude, longitude, elevation, refraction), latitude, longitude, elevation, refraction, false);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(1.0 - 69.3 / 3600.0, 1.0/3600.0);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(1.0, 1.0/3600.0);
        }


        [Test]
        public void PolarErrorDetermination_DefaultPoints_FromAstropy_TruePole() {
            var latitude = Angle.ByDegree(40);
            var longitude = Angle.ByDegree(0);
            var elevation = 250;
            var time = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            // values calculated with Astropy and quaternion rotations
            var s1 = new Coordinates(Angle.ByDegree(120.40437197), Angle.ByDegree(87.88183512), Epoch.J2000, time);
            var s2 = new Coordinates(Angle.ByDegree(133.73661393), Angle.ByDegree(87.76679023), Epoch.J2000, time);
            var s3 = new Coordinates(Angle.ByDegree(147.13178399), Angle.ByDegree(87.8022287), Epoch.J2000, time);
            var refraction = RefractionParameters.GetRefractionParameters(new Equipment.Equipment.MyWeatherData.WeatherDataInfo() { Connected = true, Pressure = 1005, Temperature = 7, Humidity = 0.8 }, 0.574);
            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = s1 },
                new Position(s3, 0, latitude, longitude, elevation, refraction),
                new Position(s2, 0, latitude, longitude, elevation, refraction),
                new Position(s1, 0, latitude, longitude, elevation, refraction), latitude, longitude, elevation, refraction, true);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(1.0, 1.0 / 3600.0);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(1.0, 1.0 / 3600.0);
        }

        [Test]
        public void PolarErrorDetermination_DefaultPoints_FromAstropy_RefractedPole() {
            var latitude = Angle.ByDegree(40);
            var longitude = Angle.ByDegree(0);
            var elevation = 250;
            var time = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            // values calculated with Astropy and quaternion rotations
            var s1 = new Coordinates(Angle.ByDegree(120.40437197), Angle.ByDegree(87.88183512), Epoch.J2000, time);
            var s2 = new Coordinates(Angle.ByDegree(133.73661393), Angle.ByDegree(87.76679023), Epoch.J2000, time);
            var s3 = new Coordinates(Angle.ByDegree(147.13178399), Angle.ByDegree(87.8022287), Epoch.J2000, time);
            var refraction = RefractionParameters.GetRefractionParameters(new Equipment.Equipment.MyWeatherData.WeatherDataInfo() { Connected = true, Pressure = 1005, Temperature = 7, Humidity = 0.8 }, 0.574);
            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = s1 },
                new Position(s3, 0, latitude, longitude, elevation, refraction),
                new Position(s2, 0, latitude, longitude, elevation, refraction),
                new Position(s1, 0, latitude, longitude, elevation, refraction), latitude, longitude, elevation, refraction, false);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(1.0 - 69/3600.0, 1.0 / 3600.0);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(1.0, 1.0 / 3600.0);
        }

    }
}