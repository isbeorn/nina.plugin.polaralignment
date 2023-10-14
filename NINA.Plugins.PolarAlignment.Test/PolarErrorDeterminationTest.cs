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
            RefrectionParameters refraction = null;

            var solve1 = new Coordinates(Angle.ByDegree(20), Angle.ByDegree(40), Epoch.JNOW).Transform(Epoch.J2000);
            var position1 = new Position(solve1, latitude, longitude, refraction);

            var solve2 = new Coordinates(Angle.ByDegree(60), Angle.ByDegree(41), Epoch.JNOW).Transform(Epoch.J2000);
            var position2 = new Position(solve2, latitude, longitude, refraction);

            var solve3 = new Coordinates(Angle.ByDegree(90), Angle.ByDegree(42), Epoch.JNOW).Transform(Epoch.J2000);
            var position3 = new Position(solve3, latitude, longitude, refraction);
            

            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() {  Coordinates = solve3 }, position1, position2, position3, latitude, longitude, refraction);

            var error2 = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = solve1 }, position3, position2, position1, latitude, longitude, refraction);

            error.InitialMountAxisTotalError.Degree.Should().NotBeApproximately(0, 0001);
            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(error2.InitialMountAxisAltitudeError.Degree, 0.0001);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(error2.InitialMountAxisAzimuthError.Degree, 0.0001);
            error.InitialMountAxisTotalError.Degree.Should().BeApproximately(error2.InitialMountAxisTotalError.Degree, 0.0001);
        }


        [Test]
        public void PolarErrorDetermination_InitialMountError_NoDeclinationError_ErrorIsZero() {
            var latitude = Angle.ByDegree(49);
            var longitude = Angle.ByDegree(7);
            RefrectionParameters refraction = null;

            var solve1 = new Coordinates(Angle.ByDegree(20), Angle.ByDegree(40), Epoch.JNOW).Transform(Epoch.J2000);
            var position1 = new Position(solve1, latitude, longitude, refraction);

            var solve2 = new Coordinates(Angle.ByDegree(60), Angle.ByDegree(40), Epoch.JNOW).Transform(Epoch.J2000);
            var position2 = new Position(solve2, latitude, longitude, refraction);

            var solve3 = new Coordinates(Angle.ByDegree(90), Angle.ByDegree(40), Epoch.JNOW).Transform(Epoch.J2000);
            var position3 = new Position(solve3, latitude, longitude, refraction);


            var error = new PolarErrorDetermination(new PlateSolving.PlateSolveResult() { Coordinates = solve1 }, position3, position2, position1, latitude, longitude, refraction);

            error.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(0, 0.0001);
            error.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(0, 0.0001);
            error.InitialMountAxisTotalError.Degree.Should().BeApproximately(0, 0.0001);
        }
    }
}