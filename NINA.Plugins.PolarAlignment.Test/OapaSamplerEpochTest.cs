using FluentAssertions;
using NINA.Astrometry;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The epoch-freezing contract of the calibration sampler. A tracked field keeps its
    /// RA/Dec while its topocentric Alt/Az drifts with sidereal time; if each sample were
    /// transformed at its own wall-clock time, that drift would be read as platform motion
    /// by every displacement comparison — the Reverse-direction check, the close against
    /// the baseline, and the best-effort restore all span minutes, enough for whole
    /// arcminutes of phantom motion near the pole. Freezing one epoch per calibration
    /// pass makes displacements mean axis motion and nothing else.
    /// </summary>
    public class OapaSamplerEpochTest {

        private static readonly Angle Latitude = Angle.ByDegree(45.5);
        private static readonly Angle Longitude = Angle.ByDegree(11.0);
        private static readonly DateTime Epoch0 = new DateTime(2026, 8, 11, 22, 0, 0, DateTimeKind.Utc);

        private static Coordinates PoleField() =>
            new Coordinates(Angle.ByHours(23.5), Angle.ByDegree(83.0), Epoch.J2000);

        [Test]
        public void UnchangedField_SolvedLater_ShowsZeroPlatformDisplacement() {
            // RA/Dec unchanged while the solve time advances by ten minutes. With both
            // samples expressed at the same frozen epoch, the measured displacement must
            // be exactly zero on both axes - the wall-clock time of the solve must not
            // enter the measurement at all.
            var first = OapaPlateSolveSampler.ToSample(PoleField(), Latitude, Longitude, Epoch0);
            var tenMinutesLater = OapaPlateSolveSampler.ToSample(PoleField(), Latitude, Longitude, Epoch0);

            OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: true, first, tenMinutesLater)
                .Should().Be(0.0, "a tracked field that has not moved must show zero azimuth-axis motion");
            OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: false, first, tenMinutesLater)
                .Should().Be(0.0, "a tracked field that has not moved must show zero altitude-axis motion");
        }

        [Test]
        public void PerSolveEpochs_WouldAliasSiderealDriftIntoPlatformMotion() {
            // What the freeze prevents, quantified: the same field transformed at epochs
            // ten minutes apart differs by whole arcminutes of Alt/Az - sky rotation that
            // the per-solve transform handed to the displacement math as axis motion.
            var atStart = OapaPlateSolveSampler.ToSample(PoleField(), Latitude, Longitude, Epoch0);
            var atLaterEpoch = OapaPlateSolveSampler.ToSample(PoleField(), Latitude, Longitude, Epoch0.AddMinutes(10));

            var driftAz = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: true, atStart, atLaterEpoch);
            var driftAlt = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: false, atStart, atLaterEpoch);

            (Math.Abs(driftAz) + Math.Abs(driftAlt)).Should().BeGreaterThan(1.0,
                "ten minutes of sidereal rotation moves a polar field's Alt/Az by arcminutes - " +
                "this is the phantom motion the frozen epoch keeps out of the measurement");
        }

        [Test]
        public void BeginCalibration_IsCalledOnTheSolver_BeforeAnySolveOfThePass() {
            // The service side of the contract: the epoch must be re-frozen for every
            // calibration pass, before the baseline solve.
            var probe = new EpochProbeSolver();
            var service = new OapaCalibrationService(probe, probe);

            var act = () => service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);
            act.Should().NotThrowAsync();

            probe.BeginCalibrationCalls.Should().BeGreaterThan(0);
            probe.FirstSolveCameAfterBegin.Should().BeTrue("the epoch must be frozen before the first sample of the pass");
        }

        private sealed class EpochProbeSolver : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private double physicalPositionArcmin;
            public int BeginCalibrationCalls { get; private set; }
            public bool FirstSolveCameAfterBegin { get; private set; } = true;
            private bool anySolve;

            public void BeginCalibration() {
                BeginCalibrationCalls++;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                physicalPositionArcmin += arcmin;
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                if (!anySolve) {
                    anySolve = true;
                    FirstSolveCameAfterBegin = BeginCalibrationCalls > 0;
                }
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, physicalPositionArcmin / 60.0, 30.0 + physicalPositionArcmin / 60.0, 0.0));
            }
        }
    }
}
