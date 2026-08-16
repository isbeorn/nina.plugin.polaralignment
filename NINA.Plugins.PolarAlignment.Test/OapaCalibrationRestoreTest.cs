using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// What the calibration promises about the PHYSICAL position of the platform when
    /// things go wrong — the claims come from an upstream review of the same code:
    ///
    /// 1. Whether a restore is needed is not the commanded sum. With backlash, the sum
    ///    returns to zero while the mechanism is still displaced (the reversal legs lose
    ///    motion the forward legs delivered); a failure at that exact moment used to skip
    ///    the restore entirely.
    /// 2. "Measured" and "physically back at the start" are different claims. A failed or
    ///    out-of-tolerance closing keeps the measured result but must report it, not
    ///    publish full success.
    /// 3. Cancellation stays a cancellation: it still triggers the best-effort restore,
    ///    and it must never be converted into an apparent success by the closing phase.
    ///
    /// The fake is a physical mechanism: scale, deadband, engagement state. Assertions are
    /// on where the axis actually ends up, not on which commands were emitted.
    /// </summary>
    public class OapaCalibrationRestoreTest {

        /// <summary>
        /// Mechanism with scale 1.6 and a 2' deadband, plus scriptable failures. The scale
        /// is chosen so the clean legs come out at 5' logical (8' physical / 1.6), which
        /// makes the commanded sum cross exactly zero after the second reverse leg:
        /// +5 (probe) +5 +5 (forward) −5 (reversal) −5 −5 (reverse legs) = 0,
        /// while the deadband keeps the physical position at 3.2' — the precise state
        /// the old commanded-sum filter mistook for "nothing to restore".
        /// </summary>
        private sealed class PhysicalFakeAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private const double Scale = 1.6;
            private const double DeadbandArcmin = 2.0;

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
                    var room = DeadbandArcmin - engagement;
                    var eaten = Math.Min(room, d);
                    engagement += eaten;
                    PhysicalArcmin += (d - eaten) * Scale;
                } else if (d < 0) {
                    var eaten = Math.Min(engagement, -d);
                    engagement -= eaten;
                    PhysicalArcmin -= (-d - eaten) * Scale;
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
                }
                return Task.FromResult(new CalibrationSolveSample(10.0, 0.0, 30.0 + PhysicalArcmin / 60.0, 0.0));
            }
        }

        private static Task<AxisCalibrationOutcome> Calibrate(PhysicalFakeAxis axis, CancellationToken token = default) {
            var service = new OapaCalibrationService(axis, axis);
            return service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, token);
        }

        // Solve schedule of a clean pass on this mechanism:
        //  1 noise, 2 baseline, 3 probe, 4 f1, 5 f2, 6 reversal, 7 r1, 8 r2, 9 opposite
        //  transition, 10+ closing verification solves.
        // Move schedule: 1 probe, 2 f1, 3 f2, 4 reversal, 5 r1, 6 r2, 7 opposite, 8+ closing.

        [Test]
        public async Task SolveFailure_WithTheCommandedSumBackAtZero_StillRestoresThePhysicalAxis() {
            // The review's exact scenario: after the second reverse leg the commanded sum
            // is 0.00' while the mechanism sits 3.2' from its baseline (deadband × scale).
            // The old `movedArcmin != 0` filter skipped the restore here.
            var axis = new PhysicalFakeAxis { ThrowOnSolve = 8 };

            var act = () => Calibrate(axis);
            await act.Should().ThrowAsync<InvalidOperationException>();

            axis.MovesAfterFailure.Should().BeGreaterThan(0, "the restore must run even though the commanded sum is zero");
            Math.Abs(axis.PhysicalArcmin).Should().BeLessThan(1.5f,
                "the platform was 3.2' off its baseline at the failure; the measured restore must drive that back");
        }

        [Test]
        public async Task SolveFailure_MidPass_RestoresBeforeRethrowing() {
            // Failure with a large commanded sum outstanding (after the forward legs):
            // the path that always restored keeps restoring.
            var axis = new PhysicalFakeAxis { ThrowOnSolve = 6 };

            var act = () => Calibrate(axis);
            await act.Should().ThrowAsync<InvalidOperationException>();

            Math.Abs(axis.PhysicalArcmin).Should().BeLessThan(1.5f);
        }

        [Test]
        public async Task ClosingMoveFailure_KeepsTheMeasuredResult_ButReportsNotRestored() {
            // The measurement is complete when the closing moves start; a closing failure
            // must not discard it - and must not be silent either.
            var axis = new PhysicalFakeAxis { ThrowFromMove = 8 };

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeGreaterThan(0, "the measured calibration survives a closing failure");
            outcome.RestoredToBaseline.Should().BeFalse();
            outcome.ClosingResidualArcmin.Should().Be(float.NaN, "the residual could not be measured");
        }

        [Test]
        public async Task VerificationSolveFailure_DuringTheClose_ReportsNotRestored() {
            var axis = new PhysicalFakeAxis { ThrowOnSolve = 10 };

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeGreaterThan(0);
            outcome.RestoredToBaseline.Should().BeFalse("the closing position was never verified");
        }

        [Test]
        public async Task Cancellation_DuringTheClose_IsPreserved_AndStillRestores() {
            // The old closing-phase catch-all swallowed OperationCanceledException and let
            // the calibration return as a success. Cancelling must stay a cancellation -
            // after the best-effort restore has run.
            var axis = new PhysicalFakeAxis { CancelOnSolve = 10 };

            var act = () => Calibrate(axis, axis.Cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();

            Math.Abs(axis.PhysicalArcmin).Should().BeLessThan(1.5f, "cancellation still drives the axis back to its baseline");
        }

        [Test]
        public async Task AResidualTheCloseCannotRemove_IsNotReportedAsRestored() {
            // The axis freezes when the closing moves start (severe stiction): every
            // closing iteration commands motion and nothing happens. Iterations exhaust
            // with the full displacement still there - that is not "restored".
            var axis = new PhysicalFakeAxis { FreezeFromMove = 8 };

            var outcome = await Calibrate(axis);

            outcome.RestoredToBaseline.Should().BeFalse();
            outcome.ClosingResidualArcmin.Should().BeGreaterThan(2f,
                "the reported residual must be the real out-of-tolerance displacement, not a hopeful zero");
        }

        [Test]
        public async Task ACleanPass_ReportsRestored_WithTheMeasuredResidual() {
            var axis = new PhysicalFakeAxis();

            var outcome = await Calibrate(axis);

            outcome.RestoredToBaseline.Should().BeTrue();
            Math.Abs(outcome.ClosingResidualArcmin).Should().BeLessThan(0.5f);
            Math.Abs(axis.PhysicalArcmin).Should().BeLessThan(0.5f, "the closing moves really did return the axis");
        }
    }
}
