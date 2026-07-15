using FluentAssertions;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.PlateSolving;
using System;
using System.Windows;

namespace NINA.Plugins.PolarAlignment.Test {
    /// <summary>
    /// Synthetic regression tests for the continuous correction estimator.
    ///
    /// These tests do not exercise UI behavior. They focus on the mathematical contract:
    /// given an initial three-point alignment solution and a later plate-solved field center,
    /// can the estimator recover the current residual polar-alignment error?
    /// </summary>
    public class ContinuousPolarErrorEstimatorTest {
        private sealed class FixedTime : ICustomDateTime {
            private readonly DateTime time;

            public FixedTime(DateTime time) {
                this.time = time;
            }

            public DateTime Now => time;

            public DateTime UtcNow => time;
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ExactRecovery_NorthernHemisphere() {
            // Baseline exact-recovery test in the northern hemisphere.
            // A synthetic mount is created with a known initial polar-axis error, then a later
            // frame is generated from a known residual error after partial correction.
            // The estimator should recover that residual essentially exactly.
            var latitude = Angle.ByDegree(48.0);
            var longitude = Angle.ByDegree(7.0);
            var elevation = 250d;
            var startTime = new DateTime(2024, 10, 1, 21, 0, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(9);

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 35.0, 55.0, 1.2, -0.7);
            var currentFrame = CreateCurrentFrame(determination, currentTime, 0.18, -0.11);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                currentFrame,
                                                                CreateRefractionParameters(),
                                                                0.7,
                                                                -0.4);

            result.Success.Should().BeTrue();
            result.AzimuthErrorDegrees.Should().BeApproximately(0.18, 0.1 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(-0.11, 0.1 / 3600.0);
            result.ResidualArcSeconds.Should().BeLessThan(0.1);
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ReportsSmallerAzimuthResidualAfterPhysicalImprovement() {
            // Direct regression for the observed failure mode: an initial +3 degree azimuth error
            // is physically improved to +2 degrees. The continuous estimate must report the smaller
            // residual, not mirror the correction to +4 degrees.
            var latitude = Angle.ByDegree(48.0);
            var longitude = Angle.ByDegree(7.0);
            var elevation = 250d;
            var startTime = new DateTime(2024, 10, 1, 21, 0, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(9);

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 35.0, 55.0, 3.0, -0.4);
            var currentFrame = CreateCurrentFrame(determination, currentTime, 2.0, -0.4);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                currentFrame,
                                                                CreateRefractionParameters(),
                                                                3.0,
                                                                -0.4);

            result.Success.Should().BeTrue();
            result.AzimuthErrorDegrees.Should().BeLessThan(determination.InitialMountAxisAzimuthError.Degree);
            result.AzimuthErrorDegrees.Should().BeApproximately(2.0, 0.1 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(-0.4, 0.1 / 3600.0);
            result.ResidualArcSeconds.Should().BeLessThan(0.1);
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ReportsSmallerSouthernAzimuthResidualAfterPhysicalImprovement() {
            // Same sign-regression as the northern case, but with southern hemisphere azimuth
            // convention: an initial -3 degree azimuth error is improved toward zero to -2 degrees.
            var latitude = Angle.ByDegree(-33.0);
            var longitude = Angle.ByDegree(151.0);
            var elevation = 40d;
            var startTime = new DateTime(2024, 11, 1, 10, 30, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(14);

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 210.0, 50.0, -3.0, 0.4);
            var currentFrame = CreateCurrentFrame(determination, currentTime, -2.0, 0.4);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                currentFrame,
                                                                CreateRefractionParameters(),
                                                                -3.0,
                                                                0.4);

            result.Success.Should().BeTrue();
            Math.Abs(result.AzimuthErrorDegrees).Should().BeLessThan(Math.Abs(determination.InitialMountAxisAzimuthError.Degree));
            result.AzimuthErrorDegrees.Should().BeApproximately(-2.0, 0.1 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(0.4, 0.1 / 3600.0);
            result.ResidualArcSeconds.Should().BeLessThan(0.1);
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ExactRecovery_SouthernHemisphere() {
            // Same exact-recovery check as above, but for southern-hemisphere sign conventions.
            // This protects against north/south regressions in azimuth direction handling and
            // altitude error sign mapping.
            var latitude = Angle.ByDegree(-33.0);
            var longitude = Angle.ByDegree(151.0);
            var elevation = 40d;
            var startTime = new DateTime(2024, 11, 1, 10, 30, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(14);

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 210.0, 50.0, -0.9, 0.6);
            var currentFrame = CreateCurrentFrame(determination, currentTime, 0.25, -0.15);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                currentFrame,
                                                                CreateRefractionParameters(),
                                                                -0.6,
                                                                0.4);

            result.Success.Should().BeTrue();
            result.AzimuthErrorDegrees.Should().BeApproximately(0.25, 0.1 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(-0.15, 0.1 / 3600.0);
            result.ResidualArcSeconds.Should().BeLessThan(0.1);
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ExactRecovery_WhenNoAdjustmentOccursAfterTimeProgression() {
            // Verifies that elapsed time alone does not look like a correction.
            // If the mount's residual error stays equal to the original error, the estimator should
            // report the same residual even though the sky has rotated and the field center changed.
            var latitude = Angle.ByDegree(40.0);
            var longitude = Angle.ByDegree(0.0);
            var elevation = 250d;
            var startTime = new DateTime(2024, 12, 1, 23, 15, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(25);

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 140.0, 68.0, 1.0, 1.0);
            var currentFrame = CreateCurrentFrame(determination,
                                                  currentTime,
                                                  determination.InitialMountAxisAzimuthError.Degree,
                                                  determination.InitialMountAxisAltitudeError.Degree);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                currentFrame,
                                                                CreateRefractionParameters(),
                                                                0.6,
                                                                0.7);

            result.Success.Should().BeTrue();
            result.AzimuthErrorDegrees.Should().BeApproximately(determination.InitialMountAxisAzimuthError.Degree, 0.1 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(determination.InitialMountAxisAltitudeError.Degree, 0.1 / 3600.0);
            result.ResidualArcSeconds.Should().BeLessThan(0.1);
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ToleratesArcsecondScalePlateSolveNoise() {
            // Plate solves are never perfect. This test perturbs the synthetic "measured" field
            // center by a few arcseconds and checks that the recovered residual remains close to truth.
            // The purpose is not exact equality, but low sensitivity to realistic solve noise.
            var latitude = Angle.ByDegree(49.0);
            var longitude = Angle.ByDegree(7.0);
            var elevation = 250d;
            var startTime = new DateTime(2024, 10, 1, 21, 0, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(6);
            var refraction = CreateRefractionParameters();

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 55.0, 42.0, 1.3, -0.8);
            var currentFrame = CreateCurrentFrame(determination, currentTime, 0.22, -0.16);
            var noisyCoordinates = PerturbCoordinate(currentFrame.Coordinates,
                                                    latitude,
                                                    longitude,
                                                    elevation,
                                                    refraction,
                                                    currentTime,
                                                    2.0 / 3600.0,
                                                    -1.5 / 3600.0);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                new PlateSolveResult() { Coordinates = noisyCoordinates },
                                                                refraction,
                                                                0.8,
                                                                -0.4);

            result.Success.Should().BeTrue();
            result.AzimuthErrorDegrees.Should().BeApproximately(0.22, 5.0 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(-0.16, 5.0 / 3600.0);
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ExactRecovery_WhenResidualIsFullyCorrected() {
            // End-state regression: if the user has fully corrected the mount, the residual should
            // solve back to zero rather than sticking near the previous seed or the original error.
            var latitude = Angle.ByDegree(47.0);
            var longitude = Angle.ByDegree(11.0);
            var elevation = 800d;
            var startTime = new DateTime(2024, 10, 18, 20, 0, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(11);

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 75.0, 58.0, 1.4, -0.9);
            var currentFrame = CreateCurrentFrame(determination, currentTime, 0.0, 0.0);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                currentFrame,
                                                                CreateRefractionParameters(),
                                                                0.45,
                                                                -0.35);

            result.Success.Should().BeTrue();
            result.AzimuthErrorDegrees.Should().BeApproximately(0.0, 0.1 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(0.0, 0.1 / 3600.0);
            result.ResidualArcSeconds.Should().BeLessThan(0.1);
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ConvergesFromPoorInitialSeed() {
            // Practical robustness test: the continuous loop seeds from the previous estimate, but
            // that estimate can be stale or wrong. The nonlinear solve should still converge from a
            // materially incorrect starting point when the geometry is well behaved.
            var latitude = Angle.ByDegree(44.0);
            var longitude = Angle.ByDegree(-93.0);
            var elevation = 280d;
            var startTime = new DateTime(2024, 8, 12, 4, 30, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(13);

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 110.0, 52.0, 1.1, 0.7);
            var currentFrame = CreateCurrentFrame(determination, currentTime, 0.14, -0.18);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                currentFrame,
                                                                CreateRefractionParameters(),
                                                                -0.9,
                                                                1.0);

            result.Success.Should().BeTrue();
            result.AzimuthErrorDegrees.Should().BeApproximately(0.14, 0.2 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(-0.18, 0.2 / 3600.0);
            result.ResidualArcSeconds.Should().BeLessThan(0.15);
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ExactRecovery_NearAzimuthWrapAround() {
            // Protect the signed shortest-path azimuth residual. The observed topocentric azimuth is
            // intentionally placed near the 0/360 discontinuity so wrapping errors would show up here.
            var latitude = Angle.ByDegree(30.0);
            var longitude = Angle.ByDegree(-17.0);
            var elevation = 2300d;
            var startTime = new DateTime(2024, 7, 4, 3, 0, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(7);

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 359.5, 42.0, 0.8, -0.4);
            var currentFrame = CreateCurrentFrame(determination, currentTime, -0.12, 0.09);
            var observed = currentFrame.Coordinates.Transform(latitude,
                                                              longitude,
                                                              elevation,
                                                              CreateRefractionParameters().PressureHPa,
                                                              CreateRefractionParameters().Temperature,
                                                              CreateRefractionParameters().RelativeHumidity,
                                                              CreateRefractionParameters().Wavelength,
                                                              currentTime);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                currentFrame,
                                                                CreateRefractionParameters(),
                                                                0.2,
                                                                -0.1);

            result.Success.Should().BeTrue();
            observed.Azimuth.Degree.Should().Match(degrees => degrees < 5 || degrees > 355);
            result.AzimuthErrorDegrees.Should().BeApproximately(-0.12, 0.2 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(0.09, 0.2 / 3600.0);
        }

        [Test]
        public void ContinuousPolarErrorEstimator_ExactRecovery_ForLowAltitudeRefractionSensitiveField() {
            // Low-altitude fields are where apparent coordinates are most affected by refraction.
            // This scenario checks that the continuous solve remains self-consistent in a regime that
            // is scientifically more demanding than the mid-altitude defaults. The test is aimed at
            // the continuous solver, so it intentionally does not require the initial three-point
            // synthetic helper to reproduce the hand-picked starting error exactly at this geometry.
            var latitude = Angle.ByDegree(35.0);
            var longitude = Angle.ByDegree(-105.0);
            var elevation = 2200d;
            var startTime = new DateTime(2024, 9, 21, 5, 0, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(8);

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 85.0, 12.0, 0.9, 0.5, verifyRecoveredInitialError: false);
            var currentFrame = CreateCurrentFrame(determination, currentTime, 0.17, 0.06);

            var result = ContinuousPolarErrorEstimator.Estimate(determination,
                                                                currentFrame,
                                                                CreateRefractionParameters(),
                                                                0.45,
                                                                0.22);

            result.Success.Should().BeTrue();
            result.AzimuthErrorDegrees.Should().BeApproximately(0.17, 0.2 / 3600.0);
            result.AltitudeErrorDegrees.Should().BeApproximately(0.06, 0.2 / 3600.0);
            result.ResidualArcSeconds.Should().BeLessThan(0.15);
        }

        [Test]
        public void PolarErrorDetermination_GetDestinationCoordinates_AppliesLegacyAdjustmentSign() {
            var latitude = Angle.ByDegree(52.0);
            var longitude = Angle.ByDegree(13.0);
            var elevation = 60d;
            var startTime = new DateTime(2024, 9, 1, 22, 0, 0, DateTimeKind.Utc);
            var refraction = CreateRefractionParameters();

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 120.0, 60.0, 1.0, 0.5);
            determination.CurrentReferenceFrame = CreateCurrentFrame(determination,
                                                                      startTime.AddMinutes(20),
                                                                      determination.InitialMountAxisAzimuthError.Degree,
                                                                      determination.InitialMountAxisAltitudeError.Degree);

            var destination = determination.GetDestinationCoordinates(-determination.InitialMountAxisAzimuthError.Degree,
                                                                      -determination.InitialMountAxisAltitudeError.Degree,
                                                                      refraction);
            var expected = GetLegacyDestinationCoordinates(determination,
                                                           -determination.InitialMountAxisAzimuthError.Degree,
                                                           -determination.InitialMountAxisAltitudeError.Degree);

            NormalizeSignedDegrees(destination.Azimuth.Degree - expected.Azimuth.Degree).Should().BeApproximately(0, 0.1 / 3600.0);
            destination.Altitude.Degree.Should().BeApproximately(expected.Altitude.Degree, 0.1 / 3600.0);
            destination.DateTime.Should().BeSameAs(determination.CurrentReferenceFrame.Coordinates.DateTime);
        }

        [Test]
        public void TPAPAErrorOverlay_UsesLegacyPointConstructionForSelectedStar() {
            // The overlay component endpoints come from the legacy correction geometry, then that
            // geometry is translated onto the selected star.
            var latitude = Angle.ByDegree(48.0);
            var longitude = Angle.ByDegree(7.0);
            var elevation = 250d;
            var startTime = new DateTime(2024, 10, 1, 21, 0, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(9);
            var refraction = CreateRefractionParameters();
            var center = new Point(2048, 1536);
            var selectedStar = new Point(3240, 680);
            var arcsecPerPix = 1.4;

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 35.0, 55.0, 3.0, -0.4);
            SetProjectionAngles(determination.InitialReferenceFrame, 18.0, 108.0);
            var currentFrame = CreateCurrentFrame(determination, currentTime, 2.0, -0.2);
            SetProjectionAngles(currentFrame, 27.0, 117.0);
            determination.CurrentReferenceFrame = currentFrame;
            determination.CurrentMountAxisAzimuthError = Angle.ByDegree(2.0);
            determination.CurrentMountAxisAltitudeError = Angle.ByDegree(-0.2);
            determination.CurrentMountAxisTotalError = Angle.ByDegree(Math.Sqrt(2.0 * 2.0 + 0.2 * 0.2));

            var referenceStarCoordinates = determination.InitialReferenceFrame.Coordinates.Shift(selectedStar.X - center.X,
                                                                                                  selectedStar.Y - center.Y,
                                                                                                  ProjectionAngle(determination.InitialReferenceFrame),
                                                                                                  arcsecPerPix,
                                                                                                  arcsecPerPix);
            var currentReferenceStar = new Point(3180, 740);

            var originPixel = determination.InitialReferenceFrame.Coordinates.XYProjection(currentFrame.Coordinates,
                                                                                           center,
                                                                                           arcsecPerPix,
                                                                                           arcsecPerPix,
                                                                                           ProjectionAngle(currentFrame));
            var pointShift = center - originPixel;
            originPixel = originPixel + pointShift * 2;

            Point ProjectDestination(double azimuthAngleDegrees, double altitudeAngleDegrees) {
                return determination.GetDestinationCoordinates(azimuthAngleDegrees,
                                                               altitudeAngleDegrees,
                                                               refraction)
                    .Transform(Epoch.J2000)
                    .XYProjection(currentFrame.Coordinates,
                                  center,
                                  arcsecPerPix,
                                  arcsecPerPix,
                                  ProjectionAngle(currentFrame)) + pointShift * 2;
            }

            var destinationPixel = ProjectDestination(-determination.InitialMountAxisAzimuthError.Degree,
                                                      -determination.InitialMountAxisAltitudeError.Degree);
            var originalAzimuthPixel = ProjectDestination(-determination.InitialMountAxisAzimuthError.Degree, 0);
            var originalAltitudePixel = ProjectDestination(0, -determination.InitialMountAxisAltitudeError.Degree);
            var correctedAzimuthPixel = Intersect(center,
                                                   originalAzimuthPixel - originPixel,
                                                   originalAzimuthPixel,
                                                   destinationPixel - originalAzimuthPixel);
            var correctedAltitudePixel = Intersect(center,
                                                    originalAltitudePixel - originPixel,
                                                    originalAltitudePixel,
                                                    destinationPixel - originalAltitudePixel);
            var shift = currentReferenceStar - center;
            var expectedInitialOrigin = originPixel + shift;
            var expectedAltitude = correctedAltitudePixel + shift;
            var expectedAzimuth = correctedAzimuthPixel + shift;
            var expectedTotal = destinationPixel + shift;
            var expectedInitialAltitude = originalAltitudePixel + shift;
            var expectedInitialAzimuth = originalAzimuthPixel + shift;

            var vm = new TPAPAVM(null, null) { ReferenceStarCoordinates = referenceStarCoordinates };

            var overlay = vm.BuildErrorDetailComputation(determination,
                                                         currentReferenceStar,
                                                         center,
                                                         arcsecPerPix,
                                                         refraction);

            const double destinationProjectionTolerance = 0.05;
            overlay.Current.Origin.X.Should().BeApproximately(currentReferenceStar.X, 0.001);
            overlay.Current.Origin.Y.Should().BeApproximately(currentReferenceStar.Y, 0.001);
            overlay.Current.Altitude.X.Should().BeApproximately(expectedAltitude.X, destinationProjectionTolerance);
            overlay.Current.Altitude.Y.Should().BeApproximately(expectedAltitude.Y, destinationProjectionTolerance);
            overlay.Current.Azimuth.X.Should().BeApproximately(expectedAzimuth.X, destinationProjectionTolerance);
            overlay.Current.Azimuth.Y.Should().BeApproximately(expectedAzimuth.Y, destinationProjectionTolerance);
            overlay.Current.Total.X.Should().BeApproximately(expectedTotal.X, destinationProjectionTolerance);
            overlay.Current.Total.Y.Should().BeApproximately(expectedTotal.Y, destinationProjectionTolerance);
            overlay.Initial.Origin.X.Should().BeApproximately(expectedInitialOrigin.X, 0.001);
            overlay.Initial.Origin.Y.Should().BeApproximately(expectedInitialOrigin.Y, 0.001);
            overlay.Initial.Altitude.X.Should().BeApproximately(expectedInitialAltitude.X, destinationProjectionTolerance);
            overlay.Initial.Altitude.Y.Should().BeApproximately(expectedInitialAltitude.Y, destinationProjectionTolerance);
            overlay.Initial.Azimuth.X.Should().BeApproximately(expectedInitialAzimuth.X, destinationProjectionTolerance);
            overlay.Initial.Azimuth.Y.Should().BeApproximately(expectedInitialAzimuth.Y, destinationProjectionTolerance);
            overlay.Initial.Total.X.Should().BeApproximately(expectedTotal.X, destinationProjectionTolerance);
            overlay.Initial.Total.Y.Should().BeApproximately(expectedTotal.Y, destinationProjectionTolerance);
            AssertParallel(overlay.Current.Azimuth - overlay.Current.Origin, overlay.Initial.Azimuth - overlay.Initial.Origin);
            AssertParallel(overlay.Current.Altitude - overlay.Current.Origin, overlay.Initial.Altitude - overlay.Initial.Origin);
        }

        [Test]
        public void TPAPAErrorOverlay_LegacyComputationKeepsImagePlaneErrorEstimate() {
            // The default live correction path is intentionally the legacy image-plane ratio/intersection
            // calculation. The continuous estimator can be enabled separately, but it must not replace
            // this observable estimate unless that option is on. The fixed current-frame clock also
            // ensures that this estimate cannot drift as the wall clock advances.
            var latitude = Angle.ByDegree(48.0);
            var longitude = Angle.ByDegree(7.0);
            var elevation = 250d;
            var startTime = new DateTime(2024, 10, 1, 21, 0, 0, DateTimeKind.Utc);
            var currentTime = startTime.AddMinutes(9);
            var refraction = CreateRefractionParameters();
            var center = new Point(2048, 1536);
            var selectedStar = new Point(3180, 740);
            var arcsecPerPix = 1.4;

            var determination = CreateDetermination(latitude, longitude, elevation, startTime, 35.0, 55.0, 3.0, -0.4);
            SetProjectionAngles(determination.InitialReferenceFrame, 18.0, 108.0);
            var currentFrame = CreateCurrentFrame(determination, currentTime, 2.0, -0.2);
            SetProjectionAngles(currentFrame, 27.0, 117.0);
            determination.CurrentReferenceFrame = currentFrame;

            var vm = new TPAPAVM(null, null);
            var overlay = vm.BuildLegacyErrorDetailComputation(determination,
                                                               selectedStar,
                                                               center,
                                                               arcsecPerPix,
                                                               refraction);

            overlay.HasErrorEstimate.Should().BeTrue();
            var azimuthErrorDegrees = overlay.AzimuthErrorDegrees.GetValueOrDefault();
            var altitudeErrorDegrees = overlay.AltitudeErrorDegrees.GetValueOrDefault();
            azimuthErrorDegrees.Should().BeApproximately(2.0075, 1e-3);
            altitudeErrorDegrees.Should().BeApproximately(-0.1853, 1e-3);
        }

        [Test]
        public void Vector3_ToTopocentric_HandlesExactAxisDirections() {
            // Low-level geometry regression: exact axis-aligned vectors used to produce incorrect
            // azimuth/altitude values in edge cases. The continuous estimator relies on this inverse
            // conversion, so these directions need explicit protection.
            var latitude = Angle.ByDegree(40);
            var longitude = Angle.ByDegree(0);
            var time = new FixedTime(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var west = new Vector3(-1, 0, 0).ToTopocentric(latitude, longitude, 0, time);
            var nadir = new Vector3(0, 0, -1).ToTopocentric(latitude, longitude, 0, time);

            west.Azimuth.Degree.Should().BeApproximately(180, 1e-9);
            west.Altitude.Degree.Should().BeApproximately(0, 1e-9);
            nadir.Altitude.Degree.Should().BeApproximately(-90, 1e-9);
        }

        private static Point Intersect(Point firstPoint, Vector firstDirection, Point secondPoint, Vector secondDirection) {
            var cross = Cross(firstDirection, secondDirection);
            cross.Should().NotBeApproximately(0, 1e-12);
            var distance = secondPoint - firstPoint;
            var t = Cross(distance, secondDirection) / cross;
            return firstPoint + firstDirection * t;
        }

        private static double Cross(Vector first, Vector second) {
            return first.X * second.Y - first.Y * second.X;
        }

        private static void AssertParallel(Vector first, Vector second) {
            var denominator = first.Length * second.Length;
            denominator.Should().BeGreaterThan(0);
            (Math.Abs(Cross(first, second)) / denominator).Should().BeLessThan(1e-9);
        }

#pragma warning disable CS0618 // The overlay regression intentionally exercises legacy projection orientation.
        private static void SetProjectionAngles(PlateSolveResult plateSolveResult, double orientation, double positionAngle) {
            plateSolveResult.Orientation = orientation;
            plateSolveResult.PositionAngle = positionAngle;
        }

        private static double ProjectionAngle(PlateSolveResult plateSolveResult) => plateSolveResult.Orientation;
#pragma warning restore CS0618

        private static TopocentricCoordinates GetLegacyDestinationCoordinates(PolarErrorDetermination determination,
                                                                              double azimuthAngleDegrees,
                                                                              double altitudeAngleDegrees) {
            var observationTime = determination.CurrentReferenceFrame.Coordinates.DateTime;
            var referenceTopocentric = determination.InitialReferenceFrame.Coordinates.Transform(determination.Latitude,
                                                                                                determination.Longitude,
                                                                                                determination.Elevation,
                                                                                                0,
                                                                                                0,
                                                                                                0,
                                                                                                0,
                                                                                                observationTime.Now);
            var referenceVector = Vector3.CoordinatesToUnitVector(referenceTopocentric);
            var azimuthRotation = Angle.ByDegree(azimuthAngleDegrees);
            var altitudeRotation = Angle.ByDegree(altitudeAngleDegrees);
            var azimuthDestination = Vector3.RotateByRodrigues(referenceVector, new Vector3(0, 0, 1), azimuthRotation);
            var altitudeAxis = Vector3.RotateByRodrigues(new Vector3(0, 1, 0), new Vector3(0, 0, 1), azimuthRotation);
            var finalDestination = Vector3.RotateByRodrigues(azimuthDestination, altitudeAxis, altitudeRotation);

            return finalDestination.ToTopocentric(determination.Latitude,
                                                  determination.Longitude,
                                                  determination.Elevation,
                                                  observationTime);
        }

        private static PolarErrorDetermination CreateDetermination(Angle latitude,
                                                                   Angle longitude,
                                                                   double elevation,
                                                                   DateTime referenceTime,
                                                                   double referenceAzimuthDegrees,
                                                                   double referenceAltitudeDegrees,
                                                                   double initialAzimuthErrorDegrees,
                                                                   double initialAltitudeErrorDegrees,
                                                                   bool verifyRecoveredInitialError = true) {
            // Build a synthetic three-point alignment dataset from a known polar axis and a known
            // reference field. This creates a "starting state" for the continuous tests:
            // - a mount axis with known alt/az error
            // - three RA-rotation measurement points
            // - the PolarErrorDetermination object that production code would normally create
            var refraction = CreateRefractionParameters();
            var time = new FixedTime(referenceTime);

            var poleAltitude = Math.Abs(latitude.Degree);
            var axisAzimuth = latitude.Degree > 0
                ? NormalizeDegrees360(initialAzimuthErrorDegrees)
                : NormalizeDegrees360(initialAzimuthErrorDegrees - 180d);
            var axisAltitude = latitude.Degree > 0
                ? poleAltitude + initialAltitudeErrorDegrees
                : poleAltitude - initialAltitudeErrorDegrees;

            var polarAxis = new TopocentricCoordinates(Angle.ByDegree(axisAzimuth), Angle.ByDegree(axisAltitude), latitude, longitude, elevation, time);
            var referenceFrame = new TopocentricCoordinates(Angle.ByDegree(referenceAzimuthDegrees), Angle.ByDegree(referenceAltitudeDegrees), latitude, longitude, elevation, time);

            var axisVector = Vector3.CoordinatesToUnitVector(polarAxis);
            var thirdVector = Vector3.CoordinatesToUnitVector(referenceFrame);
            var secondVector = Vector3.RotateByRodrigues(thirdVector, axisVector, Angle.ByDegree(30));
            var firstVector = Vector3.RotateByRodrigues(secondVector, axisVector, Angle.ByDegree(30));

            var firstTopocentric = firstVector.ToTopocentric(latitude, longitude, elevation, time);
            var secondTopocentric = secondVector.ToTopocentric(latitude, longitude, elevation, time);
            var thirdTopocentric = thirdVector.ToTopocentric(latitude, longitude, elevation, time);

            var firstCoordinates = firstTopocentric.Transform(Epoch.J2000,
                                                              refraction.PressureHPa,
                                                              refraction.Temperature,
                                                              refraction.RelativeHumidity,
                                                              refraction.Wavelength);
            var secondCoordinates = secondTopocentric.Transform(Epoch.J2000,
                                                                refraction.PressureHPa,
                                                                refraction.Temperature,
                                                                refraction.RelativeHumidity,
                                                                refraction.Wavelength);
            var thirdCoordinates = thirdTopocentric.Transform(Epoch.J2000,
                                                              refraction.PressureHPa,
                                                              refraction.Temperature,
                                                              refraction.RelativeHumidity,
                                                              refraction.Wavelength);

            var determination = new PolarErrorDetermination(new PlateSolveResult() { Coordinates = thirdCoordinates },
                                                            new Position(firstCoordinates, 0, latitude, longitude, elevation, refraction),
                                                            new Position(secondCoordinates, 0, latitude, longitude, elevation, refraction),
                                                            new Position(thirdCoordinates, 0, latitude, longitude, elevation, refraction),
                                                            latitude,
                                                            longitude,
                                                            elevation,
                                                            refraction,
                                                            true);

            if (verifyRecoveredInitialError) {
                determination.InitialMountAxisAzimuthError.Degree.Should().BeApproximately(initialAzimuthErrorDegrees, 1.0 / 3600.0);
                determination.InitialMountAxisAltitudeError.Degree.Should().BeApproximately(initialAltitudeErrorDegrees, 1.0 / 3600.0);
            }
            return determination;
        }

        private static PlateSolveResult CreateCurrentFrame(PolarErrorDetermination determination,
                                                           DateTime observationTime,
                                                           double residualAzimuthErrorDegrees,
                                                           double residualAltitudeErrorDegrees) {
            // Project the initial reference field forward to a later time under a chosen residual
            // polar-axis error. This is the synthetic "measurement" consumed by the estimator.
            var refraction = CreateRefractionParameters();
            var topocentric = determination.InitialReferenceFrame.Coordinates.Transform(determination.Latitude,
                                                                                       determination.Longitude,
                                                                                       determination.Elevation,
                                                                                       refraction.PressureHPa,
                                                                                       refraction.Temperature,
                                                                                       refraction.RelativeHumidity,
                                                                                       refraction.Wavelength,
                                                                                       observationTime);
            var vector = Vector3.CoordinatesToUnitVector(topocentric);
            var deltaAzimuthDegrees = determination.InitialMountAxisAzimuthError.Degree - residualAzimuthErrorDegrees;
            var deltaAltitudeDegrees = determination.InitialMountAxisAltitudeError.Degree - residualAltitudeErrorDegrees;
            var azimuthAdjusted = Vector3.RotateByRodrigues(vector, new Vector3(0, 0, 1), Angle.ByDegree(deltaAzimuthDegrees));
            var altitudeAxis = Vector3.RotateByRodrigues(new Vector3(0, 1, 0), new Vector3(0, 0, 1), Angle.ByDegree(deltaAzimuthDegrees));
            var correctedVector = Vector3.RotateByRodrigues(azimuthAdjusted, altitudeAxis, Angle.ByDegree(deltaAltitudeDegrees));
            var coordinates = correctedVector
                .ToTopocentric(determination.Latitude, determination.Longitude, determination.Elevation, new FixedTime(observationTime))
                .Transform(Epoch.J2000,
                           refraction.PressureHPa,
                           refraction.Temperature,
                           refraction.RelativeHumidity,
                           refraction.Wavelength);
            return new PlateSolveResult() { Coordinates = coordinates };
        }

        private static Coordinates PerturbCoordinate(Coordinates coordinates,
                                                     Angle latitude,
                                                     Angle longitude,
                                                     double elevation,
                                                     RefractionParameters refractionParameters,
                                                     DateTime observationTime,
                                                     double azimuthNoiseDegrees,
                                                     double altitudeNoiseDegrees) {
            // Apply a small local topocentric perturbation to simulate plate-solve noise in the
            // measured field center. This keeps the noise model explicit and controllable in tests.
            var topocentric = coordinates.Transform(latitude,
                                                    longitude,
                                                    elevation,
                                                    refractionParameters.PressureHPa,
                                                    refractionParameters.Temperature,
                                                    refractionParameters.RelativeHumidity,
                                                    refractionParameters.Wavelength,
                                                    observationTime);
            var vector = Vector3.CoordinatesToUnitVector(topocentric);
            var azimuthAdjusted = Vector3.RotateByRodrigues(vector, new Vector3(0, 0, 1), Angle.ByDegree(azimuthNoiseDegrees));
            var altitudeAxis = Vector3.RotateByRodrigues(new Vector3(0, 1, 0), new Vector3(0, 0, 1), Angle.ByDegree(azimuthNoiseDegrees));
            var perturbedVector = Vector3.RotateByRodrigues(azimuthAdjusted, altitudeAxis, Angle.ByDegree(altitudeNoiseDegrees));

            return perturbedVector
                .ToTopocentric(latitude, longitude, elevation, new FixedTime(observationTime))
                .Transform(Epoch.J2000,
                           refractionParameters.PressureHPa,
                           refractionParameters.Temperature,
                           refractionParameters.RelativeHumidity,
                           refractionParameters.Wavelength);
        }

        private static RefractionParameters CreateRefractionParameters() {
            return RefractionParameters.GetRefractionParameters(new Equipment.Equipment.MyWeatherData.WeatherDataInfo() {
                Connected = true,
                Pressure = 1005,
                Temperature = 7,
                Humidity = 0.8
            }, 0.574);
        }

        private static double NormalizeDegrees360(double degrees) {
            while (degrees < 0) {
                degrees += 360d;
            }

            while (degrees >= 360d) {
                degrees -= 360d;
            }

            return degrees;
        }

        private static double NormalizeSignedDegrees(double degrees) {
            while (degrees > 180d) {
                degrees -= 360d;
            }

            while (degrees < -180d) {
                degrees += 360d;
            }

            return degrees;
        }
    }
}
