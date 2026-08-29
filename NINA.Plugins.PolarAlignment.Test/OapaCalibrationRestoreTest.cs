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

            /// <summary>
            /// The play expressed as sky, which is what assertions about where the platform
            /// ends up have to be measured against. Crossing the deadband spends 2 units of
            /// command that would otherwise have delivered 2 x 1.6 = 3.2' of motion, so an
            /// axis driven to a commanded position from the opposite side arrives exactly
            /// this far short of it - on this fake and on the bench alike.
            /// </summary>
            public const double PlayAsSkyArcmin = DeadbandArcmin * Scale;

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
                commandedArcmin += arcmin;
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

            /// <summary>
            /// The controller's own idea of where the axis is: the commanded position, which
            /// is what a stepper controller reports. Deliberately NOT PhysicalArcmin - the two
            /// differ by whatever the deadband is holding, and a fake that conflated them
            /// would let an absolute move teleport the mechanism and prove nothing.
            /// </summary>
            private double commandedArcmin;

            public Task<float?> ReadPosition(Axis axis, CancellationToken token)
                => Task.FromResult<float?>((float)commandedArcmin);

            /// <summary>
            /// An absolute move is a relative one to the difference, through the same
            /// mechanism: same scale, same deadband, same engagement. Reaching a commanded
            /// position from the other direction therefore lands short by the play, exactly
            /// as it does on the bench.
            /// </summary>
            public Task MoveAbsolute(Axis axis, float position, CancellationToken token)
                => MoveRelative(axis, (float)(position - commandedArcmin), token);

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
        public async Task ASolveFailureInsideTheRestore_DoesNotDriveTheCommandedSumASecondTime() {
            // The same cancellation as the test above, so the best-effort restore runs and its
            // first move takes the axis home. Then the next verification solve dies, and the
            // blind fallback takes over.
            //
            // That fallback has to drive back what is still outstanding. The restore it is
            // taking over from has already moved the axis, so driving the whole commanded sum
            // a second time does not bring the platform home - it carries it the same distance
            // out the other side, blind, with nothing watching the sky.
            //
            // The test above is the control: identical cancellation, no dying solve, axis home.
            var axis = new PhysicalFakeAxis { CancelOnSolve = 10, ThrowOnSolve = 12 };

            var act = () => Calibrate(axis, axis.Cts.Token);
            await act.Should().ThrowAsync<Exception>();

            // Observed on this mechanism before the fix: the restore starts with the axis
            // +8.0' out, its first move brings it to +3.2', the solve dies, and the blind
            // fallback drives the whole 5.0' commanded sum again - leaving it at -9.6'.
            // Further from home than it was when the restore began, and on the far side.
            //
            // The claim, in order of what matters. First, the platform never crosses: whatever
            // is left is on the side it came from. That is the whole of the reported defect -
            // a failure path that carries the mount past its start and out the other way is
            // worse than one that stops short, because the user's next act is to look at a
            // platform that has moved somewhere nobody asked it to go.
            axis.PhysicalArcmin.Should().BeGreaterThanOrEqualTo(0.0,
                "the failure path must never carry the platform past its start and out the far side");

            // Second, what is left is no worse than the play. The axis is driven to the
            // position the controller recorded before the pass, and it gets there exactly - in
            // the controller's units. The mechanism arrives one play width short because the
            // last move reverses direction and the deadband eats the first part of it, which
            // is what any real stepper does and is not something an absolute move can undo.
            // Removing that last width means approaching from the far side on purpose, which
            // adds a move to the failure path and a moment where the axis is deliberately past
            // home - a separate change, on its own evidence, not a rider on this one.
            axis.PhysicalArcmin.Should().BeLessThanOrEqualTo(PhysicalFakeAxis.PlayAsSkyArcmin,
                "the recorded position is reached in commanded units, so what remains is the play and nothing more");

            // What this test does NOT prove, deliberately: that the absolute move to the
            // recorded position was issued at all. On this schedule the measured restore had
            // already walked the axis to the recorded start before the solve died, so a
            // failure path that simply stopped would land in the same place - measured, by
            // disabling the move and watching this test stay green. The claim here is the
            // narrower one it can actually make, which is the reported defect: the platform is
            // not carried out the far side. That the move itself happens is asserted where it
            // can fail, in MidSequenceFailure_SolverUnavailable_ReturnsToTheRecordedStartPosition,
            // where the restore never gets a chance to move the axis at all.
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
