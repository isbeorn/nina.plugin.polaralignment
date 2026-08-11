using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// What the calibration promises about the PHYSICAL position of the platform when
    /// things go wrong:
    ///
    /// 1. Whether a restore is needed is not the commanded sum. With backlash, the sum
    ///    returns to zero at the end of the leg sequence while the mechanism is still
    ///    displaced (the reversal leg lost motion the forward legs delivered); a failure
    ///    at that exact moment used to skip the restore entirely.
    /// 2. "Measured" and "physically back at the start" are different claims. A failed or
    ///    out-of-tolerance closing keeps the measured result but must report it, not
    ///    publish full success.
    /// 3. Cancellation stays a cancellation: it still triggers the best-effort restore
    ///    and must never be converted into an apparent success by the closing phase.
    ///
    /// The fake is a physical mechanism: a 10' dead-travel band with engagement state.
    /// Assertions are on where the axis actually ends up, not on which commands were
    /// emitted.
    /// </summary>
    public class OapaCalibrationRestoreTest {

        /// <summary>
        /// Dead-travel mechanism (10' band, engaged positive at start) with scriptable
        /// failures. With the default 45' legs the commanded sum crosses exactly zero
        /// after the second reverse leg (+45 +45 −45 −45) while the deadband leaves the
        /// mechanism +10' from its baseline — the precise state the commanded-sum filter
        /// mistook for "nothing to restore".
        /// </summary>
        private sealed class DeadTravelAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private const double DeadbandArcmin = 10.0;

            public double PhysicalArcmin { get; private set; }
            private double engagement = DeadbandArcmin; // engaged positive
            private int solveCount;
            private int moveCount;
            public int MovesAfterFailure { get; private set; }
            private bool failed;

            /// <summary>Solve index (1-based) that throws once; 0 = never.</summary>
            public int ThrowOnSolve { get; set; }
            /// <summary>Move index (1-based) from which moves throw; 0 = never.</summary>
            public int ThrowFromMove { get; set; }
            /// <summary>Move index (1-based) from which moves silently do nothing; 0 = never.</summary>
            public int FreezeFromMove { get; set; }
            /// <summary>Solve index (1-based) at which this token source is cancelled; 0 = never.</summary>
            public int CancelOnSolve { get; set; }
            public CancellationTokenSource Cts { get; } = new();

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                moveCount++;
                if (failed) { MovesAfterFailure++; }
                if (ThrowFromMove > 0 && moveCount >= ThrowFromMove) {
                    failed = true;
                    throw new InvalidOperationException("motor controller went away");
                }
                if (FreezeFromMove > 0 && moveCount >= FreezeFromMove) {
                    return Task.CompletedTask; // stiction: command accepted, nothing moves
                }
                double d = arcmin;
                if (d > 0) {
                    var eaten = Math.Min(DeadbandArcmin - engagement, d);
                    engagement += eaten;
                    PhysicalArcmin += d - eaten;
                } else if (d < 0) {
                    var eaten = Math.Min(engagement, -d);
                    engagement -= eaten;
                    PhysicalArcmin -= (-d - eaten);
                }
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                solveCount++;
                if (ThrowOnSolve > 0 && solveCount == ThrowOnSolve) {
                    failed = true;
                    throw new InvalidOperationException("plate solve infrastructure died");
                }
                if (CancelOnSolve > 0 && solveCount == CancelOnSolve) {
                    Cts.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                // Leg magnitudes are measured on the RA/Dec great circle and the signed
                // comparisons on Alt/Az: the physical position must show up in both.
                return Task.FromResult(new CalibrationSolveSample(10.0, PhysicalArcmin / 60.0, 30.0 + PhysicalArcmin / 60.0, 0.0));
            }
        }

        private static Task<AxisCalibrationOutcome> Calibrate(DeadTravelAxis axis, CancellationToken token = default) {
            var service = new OapaCalibrationService(axis, axis);
            return service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, token);
        }

        // Solve schedule: 1 baseline, 2 A (after prime), 3 B, 4 C, 5 D, 6 closing verify.
        // Move schedule: 1 prime +45, 2 forward +45, 3 reversal −45, 4 reverse −45, 5 closing.

        [Test]
        public async Task SolveFailure_WithTheCommandedSumBackAtZero_StillRestoresThePhysicalAxis() {
            // After the last leg the commanded sum is exactly zero while the mechanism is
            // +10' from its baseline. The old `movedArcmin != 0` filter skipped the
            // restore here and the platform silently kept the displacement.
            var axis = new DeadTravelAxis { ThrowOnSolve = 5 };

            var act = () => Calibrate(axis);
            await act.Should().ThrowAsync<InvalidOperationException>();

            axis.MovesAfterFailure.Should().BeGreaterThan(0, "the restore must run even though the commanded sum is zero");
            Math.Abs(axis.PhysicalArcmin).Should().BeLessThan(0.6,
                "the platform was 10' off its baseline at the failure; the measured restore must drive that back");
        }

        [Test]
        public async Task SolveFailure_MidPass_StillRestores() {
            // Failure with a large commanded sum outstanding: the path that always
            // restored keeps restoring. (The single-shot restore pays one deadband on its
            // own reversal; anything within that band is a successful best effort.)
            var axis = new DeadTravelAxis { ThrowOnSolve = 3 };

            var act = () => Calibrate(axis);
            await act.Should().ThrowAsync<InvalidOperationException>();

            Math.Abs(axis.PhysicalArcmin).Should().BeLessThan(10.5,
                "a 90' displacement must come back to within one deadband of the baseline");
        }

        [Test]
        public async Task ClosingMoveFailure_KeepsTheMeasuredResult_ButReportsNotRestored() {
            var axis = new DeadTravelAxis { ThrowFromMove = 5 };

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeGreaterThan(0, "the measured calibration survives a closing failure");
            outcome.RestoredToBaseline.Should().BeFalse();
            outcome.ClosingResidualArcmin.Should().Be(float.NaN, "the residual could not be measured");
        }

        [Test]
        public async Task VerificationSolveFailure_DuringTheClose_ReportsNotRestored() {
            var axis = new DeadTravelAxis { ThrowOnSolve = 6 };

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeGreaterThan(0);
            outcome.RestoredToBaseline.Should().BeFalse("the closing position was never verified");
        }

        [Test]
        public async Task Cancellation_DuringTheClose_IsPreserved_AndStillRestores() {
            // The closing-phase catch-all used to swallow OperationCanceledException and
            // let the calibration return as a success. Cancelling must stay a
            // cancellation - after the best-effort restore has run.
            var axis = new DeadTravelAxis { CancelOnSolve = 6 };

            var act = () => Calibrate(axis, axis.Cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();

            Math.Abs(axis.PhysicalArcmin).Should().BeLessThan(0.6, "cancellation still drives the axis back to its baseline");
        }

        [Test]
        public async Task AResidualTheCloseCannotRemove_IsNotReportedAsRestored() {
            // The axis freezes when the closing move starts (severe stiction): the
            // closing command is accepted and nothing happens. The verification solve
            // then measures the full displacement still there - that is not "restored".
            var axis = new DeadTravelAxis { FreezeFromMove = 5 };

            var outcome = await Calibrate(axis);

            outcome.RestoredToBaseline.Should().BeFalse();
            outcome.ClosingResidualArcmin.Should().BeApproximately(10f, 0.5f,
                "the reported residual must be the real out-of-tolerance displacement, not a hopeful zero");
        }

        [Test]
        public async Task ACleanPass_ReportsRestored_WithTheMeasuredResidual() {
            var axis = new DeadTravelAxis();

            var outcome = await Calibrate(axis);

            outcome.RestoredToBaseline.Should().BeTrue();
            Math.Abs(outcome.ClosingResidualArcmin).Should().BeLessThan(0.5f);
            Math.Abs(axis.PhysicalArcmin).Should().BeLessThan(0.5, "the closing move really did return the axis");
        }
    }
}
