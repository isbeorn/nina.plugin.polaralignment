using FluentAssertions;
using NINA.Astrometry;
using NINA.Profile;
using NINA.WPF.Base.Mediator;
using static NINA.Equipment.Equipment.MyGPS.PegasusAstro.UnityApi.DriverUranusReport;

namespace NINA.Plugins.PolarAlignment.Test {
    public class PolarErrorDeterminationTest {
        [SetUp]
        public void Setup() {
        }
        
        [Test]
        public void PolarErrorDetermination_InitialMountError_SomeDeclinationError_ErrorIsEquals_ForBothPointDirections() {
            var latitude = Angle.ByDegree(49);
            var longitude = Angle.ByDegree(7);
            RefrectionParameters refraction = new RefrectionParameters(0, 0.0001, 20, 0.5);

            var solve1 = new Coordinates(Angle.ByDegree(20), Angle.ByDegree(40), Epoch.JNOW).Transform(Epoch.J2000);
            var position1 = new Position(solve1, 0, latitude, longitude, refraction);

            var solve2 = new Coordinates(Angle.ByDegree(60), Angle.ByDegree(41), Epoch.JNOW).Transform(Epoch.J2000);
            var position2 = new Position(solve2, 0, latitude, longitude, refraction);

            var solve3 = new Coordinates(Angle.ByDegree(90), Angle.ByDegree(42), Epoch.JNOW).Transform(Epoch.J2000);
            var position3 = new Position(solve3, 0, latitude, longitude, refraction);
            

            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() {  Coordinates = solve3 }, position1, position2, position3, latitude, longitude, refraction, false);

            var error2 = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = solve1 }, position3, position2, position1, latitude, longitude, refraction, false);

            error.InitialMountAxisTotalError.Degree.Should().NotBeApproximately(0, 0001);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(error2.InitialMountAxisAltitudeError.Degree, 0.0005);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(error2.InitialMountAxisAzimuthError.Degree, 0.0005);
            error.InitialMountAxisTotalError.Degree.Should().BeApproximately(error2.InitialMountAxisTotalError.Degree, 0.0005);
        }


        [Test]
        public void PolarErrorDetermination_InitialMountError_NoDeclinationError_ErrorIsZero() {
            var latitude = Angle.ByDegree(49);
            var longitude = Angle.ByDegree(7);
            RefrectionParameters refraction = new RefrectionParameters(0, 0.0001, 20, 0.5);

            var solve1 = new Coordinates(Angle.ByDegree(20), Angle.ByDegree(40), Epoch.JNOW).Transform(Epoch.J2000);
            var position1 = new Position(solve1, 0,latitude, longitude, refraction);

            var solve2 = new Coordinates(Angle.ByDegree(60), Angle.ByDegree(40), Epoch.JNOW).Transform(Epoch.J2000);
            var position2 = new Position(solve2, 0, latitude, longitude, refraction);

            var solve3 = new Coordinates(Angle.ByDegree(90), Angle.ByDegree(40), Epoch.JNOW).Transform(Epoch.J2000);
            var position3 = new Position(solve3, 0, latitude, longitude, refraction);


            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = solve1 }, position3, position2, position1, latitude, longitude, refraction, false);

            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(0, 0.0005);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(0, 0.0005);
            error.InitialMountAxisTotalError.Degree.Should().BeApproximately(0, 0.0005);
        }
        
        [Test]
        public void PolarErrorDetermination_VeryDifferentAltitudePoints_TruePole() {
            var latitude = Angle.ByDegree(40);
            var longitude = Angle.ByDegree(0);
            // prepare a misaligned mount polar axis 
            var polarAxis = new TopocentricCoordinates(Angle.ByDegree(1), latitude + Angle.ByDegree(1), latitude, longitude);
            // and a first alt-az position for the mount
            var f1 = new TopocentricCoordinates(Angle.ByDegree(70), Angle.ByDegree(20), latitude, longitude);
            var v1 = Vector3.CoordinatesToUnitVector(f1);
            var p = Vector3.CoordinatesToUnitVector(polarAxis);
            // convert into unit-norm vectors, and rotate by Rodrigues around the mount polar axis, 30 deg to the West
            var v2 = Vector3.RotateByRodrigues(v1, p, Angle.ByDegree(-30));
            var v3 = Vector3.RotateByRodrigues(v2, p, Angle.ByDegree(-30));
            var f2 = v2.ToTopocentric(latitude, longitude);
            var f3 = v3.ToTopocentric(latitude, longitude);
            // transform the three alt-az position into equatorial coordinates, accounting for refraction,
            // these three equatorial positions simulate the three plate-solved fields
            var s1 = f1.Transform(Epoch.J2000, 1013, 20, 0.5, 0.55);
            var s2 = f2.Transform(Epoch.J2000, 1013, 20, 0.5, 0.55);
            var s3 = f3.Transform(Epoch.J2000, 1013, 20, 0.5, 0.55);
            // verify that, if neglecting refraction, the three alt-az position corresponding to the
            // three simulated fields would be lower in altitude than the initial ones
            var q1 = s1.Transform(latitude, longitude);
            var q2 = s2.Transform(latitude, longitude);
            var q3 = s3.Transform(latitude, longitude);
            q1.Altitude.Degree.Should().BeLessThan(f1.Altitude.Degree);
            q2.Altitude.Degree.Should().BeLessThan(f2.Altitude.Degree);
            q3.Altitude.Degree.Should().BeLessThan(f3.Altitude.Degree);
            var refraction = new RefrectionParameters(0, 1013, 20, 0.5);
            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = s1 }, 
                new Position(s3, 0, latitude, longitude, refraction), 
                new Position(s2, 0, latitude, longitude, refraction), 
                new Position(s1, 0, latitude, longitude, refraction), latitude, longitude, refraction, true);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(1.0, 0.0005);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(1.0, 0.0005);
        }

        [Test]
        public void PolarErrorDetermination_VeryDifferentAltitudePoints_RefractedPole() {
            var latitude = Angle.ByDegree(40);
            var longitude = Angle.ByDegree(0);
            // prepare a misaligned mount polar axis 
            var polarAxis = new TopocentricCoordinates(Angle.ByDegree(1), latitude + Angle.ByDegree(1), latitude, longitude);
            // and a first alt-az position for the mount
            var f1 = new TopocentricCoordinates(Angle.ByDegree(70), Angle.ByDegree(20), latitude, longitude);
            var v1 = Vector3.CoordinatesToUnitVector(f1);
            var p = Vector3.CoordinatesToUnitVector(polarAxis);
            // convert into unit-norm vectors, and rotate by Rodrigues around the mount polar axis, 30 deg to the West
            var v2 = Vector3.RotateByRodrigues(v1, p, Angle.ByDegree(-30));
            var v3 = Vector3.RotateByRodrigues(v2, p, Angle.ByDegree(-30));
            var f2 = v2.ToTopocentric(latitude, longitude);
            var f3 = v3.ToTopocentric(latitude, longitude);
            // transform the three alt-az position into equatorial coordinates, accounting for refraction,
            // these three equatorial positions simulate the three plate-solved fields
            var s1 = f1.Transform(Epoch.J2000, 1013, 20, 0.5, 0.55);
            var s2 = f2.Transform(Epoch.J2000, 1013, 20, 0.5, 0.55);
            var s3 = f3.Transform(Epoch.J2000, 1013, 20, 0.5, 0.55);
            // verify that, if neglecting refraction, the three alt-az position corresponding to the
            // three simulated fields would be lower in altitude than the initial ones
            var q1 = s1.Transform(latitude, longitude);
            var q2 = s2.Transform(latitude, longitude);
            var q3 = s3.Transform(latitude, longitude);
            q1.Altitude.Degree.Should().BeLessThan(f1.Altitude.Degree);
            q2.Altitude.Degree.Should().BeLessThan(f2.Altitude.Degree);
            q3.Altitude.Degree.Should().BeLessThan(f3.Altitude.Degree);
            var refraction = new RefrectionParameters(0, 1013, 20, 0.5);
            //refraction = null;
            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = s1 },
                new Position(s3, 0, latitude, longitude, refraction),
                new Position(s2, 0, latitude, longitude, refraction),
                new Position(s1, 0, latitude, longitude, refraction), latitude, longitude, refraction, false);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately((1.0 - 66 / 3600.0), 0.0005);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(1.0, 0.0005);

        }
    }
}