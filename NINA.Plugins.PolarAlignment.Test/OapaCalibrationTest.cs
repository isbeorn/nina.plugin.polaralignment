using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    public class OapaCalibrationGeometryTest {

        private static CalibrationSolveSample Sample(double raDeg, double decDeg, double altDeg = 30, double azDeg = 100) =>
            new(raDeg, decDeg, altDeg, azDeg);

        [Test]
        public void AngularSeparation_OneDegreeOfDec_IsOneDegree() {
            var a = Sample(10, 0);
            var b = Sample(10, 1);
            OapaCalibrationGeometry.AngularSeparationDegrees(a, b).Should().BeApproximately(1.0, 1e-6);
        }

        [Test]
        public void AxisDisplacement_AltitudeAxis_TransfersOneToOne() {
            var a = Sample(10, 0, altDeg: 60);
            var b = Sample(10, 0.5, altDeg: 60);
            OapaCalibrationGeometry.AxisDisplacementArcmin(isAzimuthAxis: false, a, b).Should().BeApproximately(30.0, 0.01);
        }

        [Test]
        public void AxisDisplacement_AzimuthAxis_CorrectsForCosAltitude() {
            // At altitude 60°, cos(alt)=0.5: a 30' sky displacement means 60' of axis motion.
            var a = Sample(10, 0, altDeg: 60);
            var b = Sample(10, 0.5, altDeg: 60);
            OapaCalibrationGeometry.AxisDisplacementArcmin(isAzimuthAxis: true, a, b).Should().BeApproximately(60.0, 0.05);
        }

        [Test]
        public void AxisDisplacement_AzimuthNearZenith_Throws() {
            var a = Sample(10, 0, altDeg: 80);
            var b = Sample(10, 0.5, altDeg: 80);
            var act = () => OapaCalibrationGeometry.AxisDisplacementArcmin(isAzimuthAxis: true, a, b);
            act.Should().Throw<InvalidOperationException>().WithMessage("*zenith*");
        }

        [Test]
        public void SignedDisplacement_AltitudeRisesOnPositiveCommand_IsConsistent() {
            var a = Sample(10, 0, altDeg: 30);
            var b = Sample(10, 0.5, altDeg: 30.75);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, 45f).Should().BeTrue();
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, -45f).Should().BeFalse();
        }

        [Test]
        public void SignedDisplacement_AltitudeFallsOnPositiveCommand_IsInconsistent() {
            // A physically reversed altitude axis: the field drops when commanded up.
            var a = Sample(10, 0, altDeg: 30);
            var b = Sample(10, -0.5, altDeg: 29.25);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, 45f).Should().BeFalse();
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, -45f).Should().BeTrue();
        }

        [Test]
        public void SignedDisplacement_AzimuthUsesTopocentricAzimuth() {
            var a = Sample(10, 0, azDeg: 100.0);
            var b = Sample(10.5, 0, azDeg: 100.75);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, a, b, 45f).Should().BeTrue();
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, a, b, -45f).Should().BeFalse();
        }

        [Test]
        public void SignedDisplacement_AzimuthAcrossNorth_KeepsItsSign() {
            // 359.9° -> 0.65° is +0.75° of azimuth, not -359.25°: the wrap must not flip the verdict.
            var a = Sample(10, 0, azDeg: 359.9);
            var b = Sample(10.5, 0, azDeg: 0.65);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, a, b, 45f).Should().BeTrue();

            var back = OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, b, a, 45f);
            back.Should().BeFalse("0.65° -> 359.9° is negative azimuth motion");
        }

        [Test]
        public void ComputeAxisCalibration_CleanLegs_YieldRatioAndZeroBacklash() {
            // Commanded 45' with ratio 100 moved the sky by 45' on every leg: ratio confirmed, no backlash.
            var r = OapaCalibrationGeometry.ComputeAxisCalibration(45f, 100f, 45.0, 45.0, 45.0, directionConsistent: true);
            r.Ratio.Should().BeApproximately(100f, 0.01f);
            r.BacklashArcmin.Should().Be(0f);
            r.Consistent.Should().BeTrue();
            r.Asymmetric.Should().BeFalse();
        }

        [Test]
        public void ComputeAxisCalibration_ReversalShortfall_IsMeasuredAsBacklash() {
            // Clean legs 45', reversal leg only 40': 5' lost to backlash.
            var r = OapaCalibrationGeometry.ComputeAxisCalibration(45f, 100f, 45.0, 40.0, 45.0, directionConsistent: true);
            r.BacklashArcmin.Should().BeApproximately(5f, 0.01f);
        }

        [Test]
        public void ComputeAxisCalibration_RatioScalesWithMeasuredMotion() {
            // Commanded 45' only moved the sky 22.5': the true ratio is double the current one.
            var r = OapaCalibrationGeometry.ComputeAxisCalibration(45f, 100f, 22.5, 22.5, 22.5, directionConsistent: true);
            r.Ratio.Should().BeApproximately(200f, 0.01f);
        }

        [Test]
        public void ComputeAxisCalibration_WrongRatio_BacklashIsPhysical() {
            // Regression for the coordinate-system bug: current ratio 100, true ratio 200.
            // Commanded 45' legs physically move 22.5'; the reversal moves 20', so the
            // physical backlash is 2.5'. The old formula returned 5' (the shortfall scaled
            // back into the obsolete command system), which after Apply would double the
            // compensation moves.
            var r = OapaCalibrationGeometry.ComputeAxisCalibration(45f, 100f, 22.5, 20.0, 22.5, directionConsistent: true);
            r.Ratio.Should().BeApproximately(200f, 0.01f);
            r.BacklashArcmin.Should().BeApproximately(2.5f, 0.01f);
        }

        [Test]
        public void ComputeAxisCalibration_BacklashEqualsPhysicalShortfallAcrossRatios() {
            // Invariant: whatever the ratio error, BacklashArcmin is the physical shortfall
            // clean - reversal, so compensating by it under the discovered ratio reproduces
            // exactly the lost physical motion.
            foreach (var (currentRatio, trueRatio, physicalBacklash) in new[] {
                (100f, 100f, 5.0), (100f, 200f, 2.5), (200f, 100f, 10.0), (50f, 150f, 1.0) }) {
                var clean = 45.0 * currentRatio / trueRatio;
                var reversal = clean - physicalBacklash;
                var r = OapaCalibrationGeometry.ComputeAxisCalibration(45f, currentRatio, clean, reversal, clean, directionConsistent: true);
                r.BacklashArcmin.Should().BeApproximately((float)physicalBacklash, 0.01f,
                    $"currentRatio={currentRatio}, trueRatio={trueRatio}");
            }
        }

        [Test]
        public void ComputeAxisCalibration_ExcessiveBacklash_ClampsAgainstPhysicalLeg() {
            // With a wrong ratio the physical legs are 22.5'; a 17.5' shortfall exceeds half
            // of the physical leg and must clamp against it, not against the commanded 45'.
            var warnings = new List<string>();
            var r = OapaCalibrationGeometry.ComputeAxisCalibration(45f, 100f, 22.5, 5.0, 22.5, directionConsistent: true, warnings.Add);
            r.BacklashArcmin.Should().BeApproximately(11.25f, 0.01f);
            warnings.Should().ContainSingle(w => w.Contains("clamping"));
        }

        [Test]
        public void ComputeAxisCalibration_LegAsymmetryAboveThreshold_IsFlagged() {
            var r = OapaCalibrationGeometry.ComputeAxisCalibration(45f, 100f, 45.0, 40.0, 30.0, directionConsistent: true);
            r.Asymmetric.Should().BeTrue();
        }

        [Test]
        public void ComputeAxisCalibration_ExcessiveBacklash_IsClampedWithWarning() {
            var warnings = new List<string>();
            var r = OapaCalibrationGeometry.ComputeAxisCalibration(45f, 100f, 45.0, 10.0, 45.0, directionConsistent: true, warnings.Add);
            r.BacklashArcmin.Should().Be(22.5f);
            warnings.Should().ContainSingle(w => w.Contains("clamping"));
        }

        [Test]
        public void ComputeAxisCalibration_NoMeasurableMotion_Throws() {
            var act = () => OapaCalibrationGeometry.ComputeAxisCalibration(45f, 100f, 0.05, 0.0, 0.05, directionConsistent: true);
            act.Should().Throw<InvalidOperationException>().WithMessage("*did not move*");
        }
    }

    public class OapaCalibrationServiceTest {

        /// <summary>
        /// Simulates an axis with a physical response and backlash. The backlash lives in the
        /// mechanics, so it is expressed in physical arcminutes and subtracted after the
        /// response scaling. A direction sign of -1 models physically inverted wiring: the
        /// axis moves opposite to the commanded direction. Solves report the accumulated
        /// physical position as a Dec offset and as topocentric altitude (1:1 for the
        /// altitude axis).
        /// </summary>
        private sealed class FakeAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly double responseScale;
            private readonly double physicalBacklashArcmin;
            private readonly int directionSign;
            private double physicalPositionArcmin;
            private int lastSign;
            public readonly List<float> CommandedMoves = new();

            public FakeAxis(double responseScale, double physicalBacklashArcmin, int directionSign = 1) {
                this.responseScale = responseScale;
                this.physicalBacklashArcmin = physicalBacklashArcmin;
                this.directionSign = directionSign;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                CommandedMoves.Add(arcmin);
                var sign = Math.Sign(arcmin);
                double effective = Math.Abs(arcmin) * responseScale;
                if (sign != 0 && lastSign != 0 && sign != lastSign) {
                    effective = Math.Max(0, effective - physicalBacklashArcmin);
                }
                if (sign != 0) { lastSign = sign; }
                physicalPositionArcmin += sign * directionSign * effective;
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, physicalPositionArcmin / 60.0, 30.0 + physicalPositionArcmin / 60.0, 100.0));
            }
        }

        [Test]
        public async Task Calibration_RecoversResponseAndPhysicalBacklash() {
            // The axis physically moves half of what is commanded and its mechanics lose
            // 5 physical arcminutes on reversal. The discovered backlash must be those
            // 5 physical arcminutes — not the shortfall re-expressed in the obsolete
            // command system (which would be 10').
            var axis = new FakeAxis(responseScale: 0.5, physicalBacklashArcmin: 5.0);
            var service = new OapaCalibrationService(axis, axis);

            var outcome = await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, currentRatio: 100f, reversed: false, "Y", null, CancellationToken.None);

            outcome.Ratio.Should().BeApproximately(200f, 1f, "half the motion means double the factor");
            outcome.BacklashArcmin.Should().BeApproximately(5f, 0.5f, "backlash is physical axis arcminutes");
            outcome.Consistent.Should().BeTrue();
            outcome.Flipped.Should().BeFalse();
        }

        [Test]
        public async Task Calibration_InvertedPhysicalResponse_FlipsAndSucceeds() {
            // The axis physically moves opposite to the commanded direction. The first pass
            // must detect the direction mismatch, the auto-flip retry must succeed, and the
            // outcome must report Flipped so the caller persists the corrected Reverse flag.
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 0.0, directionSign: -1);
            var service = new OapaCalibrationService(axis, axis);

            var outcome = await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, currentRatio: 100f, reversed: false, "Y", null, CancellationToken.None);

            outcome.Flipped.Should().BeTrue("an inverted physical response must be resolved by flipping Reverse");
            outcome.Consistent.Should().BeTrue();
            outcome.Ratio.Should().BeApproximately(100f, 1f);
        }

        private sealed class AlwaysNegativeAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private double physicalPositionArcmin;
            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                physicalPositionArcmin -= Math.Abs(arcmin);
                return Task.CompletedTask;
            }
            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, physicalPositionArcmin / 60.0, 30.0 + physicalPositionArcmin / 60.0, 100.0));
            }
        }

        [Test]
        public async Task Calibration_DirectionUnresolvableByFlip_ReportsInconsistent() {
            // An axis that always drifts the same way no matter the command: flipping Reverse
            // cannot fix it, so the outcome must surface the inconsistency without a flip.
            var axis = new AlwaysNegativeAxis();
            var service = new OapaCalibrationService(axis, axis);

            var outcome = await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, currentRatio: 100f, reversed: false, "Y", null, CancellationToken.None);

            outcome.Flipped.Should().BeFalse();
            outcome.Consistent.Should().BeFalse();
        }

        [Test]
        public async Task Calibration_NetCommandedMotionIsZero() {
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 0.0);
            var service = new OapaCalibrationService(axis, axis);

            await service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);

            float net = 0;
            axis.CommandedMoves.ForEach(m => net += m);
            net.Should().Be(0f, "the axis must end where it started");
        }

        private sealed class FailingSolver : IOapaCalibrationSolver {
            private readonly IOapaCalibrationSolver inner;
            private int calls;
            private readonly int failFrom;
            public FailingSolver(IOapaCalibrationSolver inner, int failFrom) { this.inner = inner; this.failFrom = failFrom; }
            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                calls++;
                if (calls >= failFrom) { throw new InvalidOperationException("solver failed"); }
                return inner.CaptureAndSolve(token);
            }
        }

        [Test]
        public async Task MidSequenceFailure_DrivesAccumulatedMotionBack() {
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 0.0);
            // Baseline and solve A succeed; solve B fails, leaving two commanded legs outstanding.
            var service = new OapaCalibrationService(axis, new FailingSolver(axis, failFrom: 3));

            var act = () => service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();

            float net = 0;
            axis.CommandedMoves.ForEach(m => net += m);
            net.Should().Be(0f, "the restore move must return the axis to its start");
        }

        [Test]
        public void BaselineNearZenith_AbortsAzimuthBeforeAnyMotion() {
            var axis = new FakeAxis(responseScale: 1.0, physicalBacklashArcmin: 0.0);
            var zenithSolver = new ZenithSolver();
            var service = new OapaCalibrationService(axis, zenithSolver);

            var act = () => service.CalibrateAxisWithAutoReverse(Axis.XAxis, 100f, false, "X", null, CancellationToken.None);
            act.Should().ThrowAsync<InvalidOperationException>();
            axis.CommandedMoves.Should().BeEmpty("no motion may be commanded when the baseline field is unusable");
        }

        private sealed class ZenithSolver : IOapaCalibrationSolver {
            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) =>
                Task.FromResult(new CalibrationSolveSample(10.0, 0.0, 85.0, 100.0));
        }
    }
}
