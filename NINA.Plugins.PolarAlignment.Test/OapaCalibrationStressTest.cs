using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Stress coverage for the calibration sequence: rigs that exercise several escalation
    /// paths at once, and rigs built to break a specific internal assumption. The regular
    /// service tests prove the sequence works on plausible mechanics; these prove it stays
    /// within its own budgets and caps when every stage escalates together.
    /// </summary>
    public class OapaCalibrationStressTest {

        /// <summary>
        /// Axis simulator for pathological mechanics. Beyond the response/backlash model of
        /// the robust fake it adds two effects seen in the field:
        ///
        /// - stiction: commands below a threshold move nothing at all, so the engagement
        ///   probe has to escalate several times before anything is measurable;
        /// - a one-off loss on the first command after engagement (the stick-slip release),
        ///   which makes the first clean leg read short while the following ones are honest.
        /// </summary>
        private sealed class StressFakeAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly double forwardScale;
            private readonly double reverseScale;
            private readonly double[] backlashSequence;
            private readonly double stictionArcmin;
            private readonly double firstMoveLossArcmin;
            /// <summary>Commands at these indices deliver nothing: a clutch that grabs intermittently.</summary>
            private readonly HashSet<int> deadCommands;
            private readonly double noiseAmplitudeArcmin;
            /// <summary>Sky motion between solves that no command asked for: a mount that is not tracking, or still settling.</summary>
            private readonly double driftPerSolveArcmin;
            private readonly int physicalSign;
            private readonly double fieldAzimuthDegrees;
            private uint rng;
            private int reversals;
            private int moves;
            private double physicalPositionArcmin;
            private double peakSkyExcursionArcmin;
            private double largestDeliveredStepArcmin;
            private int lastSign;

            public int SolveCount { get; private set; }
            /// <summary>One per calibration pass, so a test can tell a single pass from an auto-flip retry.</summary>
            public int PassCount { get; private set; }
            public readonly List<float> CommandedMoves = new();
            /// <summary>Where the axis stood on the sky after each command, for reading a run back.</summary>
            public readonly List<double> SkyTrace = new();

            public void BeginCalibration() => PassCount++;

            public StressFakeAxis(double forwardScale,
                                  double? reverseScale = null,
                                  double[] backlashSequence = null,
                                  double stictionArcmin = 0,
                                  double firstMoveLossArcmin = 0,
                                  double noiseAmplitudeArcmin = 0,
                                  double driftPerSolveArcmin = 0,
                                  int seed = 4711,
                                  int physicalSign = 1,
                                  double fieldAzimuthDegrees = 0.0,
                                  int[] deadCommands = null) {
                this.driftPerSolveArcmin = driftPerSolveArcmin;
                this.forwardScale = forwardScale;
                this.reverseScale = reverseScale ?? forwardScale;
                this.backlashSequence = backlashSequence ?? new[] { 0.0 };
                this.stictionArcmin = stictionArcmin;
                this.firstMoveLossArcmin = firstMoveLossArcmin;
                this.deadCommands = new HashSet<int>(deadCommands ?? Array.Empty<int>());
                this.noiseAmplitudeArcmin = noiseAmplitudeArcmin;
                this.physicalSign = physicalSign;
                this.fieldAzimuthDegrees = fieldAzimuthDegrees;
                rng = (uint)seed;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                var commandIndex = CommandedMoves.Count;
                CommandedMoves.Add(arcmin);
                var sign = Math.Sign(arcmin);
                if (sign == 0) { return Task.CompletedTask; }

                if (deadCommands.Contains(commandIndex)) {
                    // The clutch let go for this command: the motor turned, the axis did not.
                    return Task.CompletedTask;
                }

                if (Math.Abs(arcmin) < stictionArcmin) {
                    // Below break-away: the drive train takes the command and delivers nothing.
                    return Task.CompletedTask;
                }

                var scale = sign >= 0 ? forwardScale : reverseScale;
                double effective = Math.Abs(arcmin) * scale;
                if (lastSign != 0 && sign != lastSign) {
                    var backlash = backlashSequence[reversals % backlashSequence.Length];
                    reversals++;
                    effective = Math.Max(0, effective - backlash);
                }
                if (moves++ == 0) {
                    effective = Math.Max(0, effective - firstMoveLossArcmin);
                }
                lastSign = sign;
                physicalPositionArcmin += physicalSign * sign * effective;
                // Peak excursion measured the way the service bounds it: on the sky, not on
                // the commanded sum. A wrong factor makes commanded arcminutes meaningless,
                // and the budget check runs after each move - so the peak that matters is the
                // one right here, before the next solve can see it.
                var projection = Math.Cos(fieldAzimuthDegrees * Math.PI / 180.0);
                SkyTrace.Add(physicalPositionArcmin * projection);
                peakSkyExcursionArcmin = Math.Max(peakSkyExcursionArcmin, Math.Abs(physicalPositionArcmin * projection));
                largestDeliveredStepArcmin = Math.Max(largestDeliveredStepArcmin, Math.Abs(effective * projection));
                return Task.CompletedTask;
            }

            private double NextNoise() {
                if (noiseAmplitudeArcmin <= 0) { return 0; }
                rng = rng * 1664525u + 1013904223u;
                return ((rng >> 8) / (double)(1 << 24) - 0.5) * 2 * noiseAmplitudeArcmin;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                SolveCount++;
                var projection = Math.Cos(fieldAzimuthDegrees * Math.PI / 180.0);
                var observed = physicalPositionArcmin * projection + NextNoise() + SolveCount * driftPerSolveArcmin;
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, observed / 60.0, 30.0 + observed / 60.0, fieldAzimuthDegrees));
            }

            public double PhysicalPositionArcmin => physicalPositionArcmin;
            public double PeakSkyExcursionArcmin => peakSkyExcursionArcmin;
            public double LargestDeliveredStepArcmin => largestDeliveredStepArcmin;
            public float LargestCommand => CommandedMoves.Count == 0 ? 0f : CommandedMoves.Max(Math.Abs);
        }

        private static Task<AxisCalibrationOutcome> Calibrate(StressFakeAxis axis, float currentRatio = 100f, bool reversed = false) {
            var service = new OapaCalibrationService(axis, axis);
            return service.CalibrateAxisWithAutoReverse(Axis.YAxis, currentRatio, reversed, "Y", null, CancellationToken.None);
        }

        /// <summary>The single-command cap the sequence applies to its own legs: three calibration steps.</summary>
        private const float SingleCommandCapArcmin = 3f * 45f;

        /// <summary>How close to its baseline a pass must get before it may call itself restored.</summary>
        private const double RestoreToleranceArcmin = 0.5;

        [Test]
        public async Task AnAxisWithBreakAwayFriction_IsNotMisdiagnosedAsDead() {
            // Break-away friction: nothing moves until the command is large enough, so the
            // probe escalates to 135' and the axis engages there. Sizing the clean legs from
            // the measured response alone asks for 34' - below break-away - so both legs read
            // zero and the pass fails with "axis did not move measurably; check the clutch
            // and the motor current". The rig moves perfectly well; the sequence had simply
            // asked for less than what it had just proven to work.
            var axis = new StressFakeAxis(
                forwardScale: 0.25,
                stictionArcmin: 50,
                backlashSequence: new[] { 6.0 });

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeApproximately(400f, 40f, "a quarter of the commanded motion means four times the factor");
            // The rig cannot be positioned finer than its own break-away command (50' of
            // command, 12.5' of sky), so the closing loop stops at a residual within that
            // and reports it, instead of claiming a verified return it cannot demonstrate.
            outcome.ClosingResidualArcmin.Should().NotBe(float.NaN);
            Math.Abs(outcome.ClosingResidualArcmin).Should().BeLessThan(12.5f);
        }

        [Test]
        public async Task AnAxisThatEscalatesEveryStage_LeavesHeadroomForTheClosingLoop() {
            // Every escalation path at once: break-away friction forces repeated engagement
            // probes and a backlash that grows per reversal forces the backlash leg to
            // escalate. What this locks down is the interaction between the stage limits and
            // the solve budget: the closing loop runs last, so a budget sized without the
            // stages' worst case in mind runs out exactly there - the calibration would be
            // measured, the platform left displaced, and the log would blame a sky excursion
            // while the axis was on its way home.
            var axis = new StressFakeAxis(
                forwardScale: 0.25,
                stictionArcmin: 20,
                firstMoveLossArcmin: 4,
                backlashSequence: new[] { 6.0, 25.0, 60.0, 60.0 });

            var outcome = await Calibrate(axis);

            axis.SolveCount.Should().BeLessThan(21,
                "the budget must not be the binding constraint on a rig that is behaving as designed; "
                + $"this one measured a factor of {outcome.Ratio:F0} and stopped {axis.PhysicalPositionArcmin:F2}' from its baseline");
            outcome.ClosingResidualArcmin.Should().NotBe(float.NaN,
                "the closing loop must report what it measured, whether or not it got home");
        }

        [Test]
        public async Task AResponseUnderestimatedByStiction_NeverCommandsMoreThanTheSingleLegCap() {
            // The escalated backlash leg is sized from the measured forward response, and
            // that measurement is exactly what stiction corrupts: reading the response 10x
            // low makes a leg that looks like 90' of sky worth 900' of it. Every commanded
            // leg must stay under the same absolute cap the clean legs already respect, so
            // a wrong response can cost extra iterations but never an oversized move.
            var axis = new StressFakeAxis(
                forwardScale: 0.03,
                stictionArcmin: 40,
                backlashSequence: new[] { 1.5, 4.0, 12.0 });

            try {
                await Calibrate(axis);
            } catch (InvalidOperationException) {
                // An honest abort is an acceptable outcome here; an oversized command is not.
            }

            axis.LargestCommand.Should().BeLessThanOrEqualTo(SingleCommandCapArcmin,
                "no single commanded leg may exceed the cap, whatever the measured response says");
        }

        [Test]
        public async Task WhenTheFirstCleanLegIsTheOutlier_TheClosingMovesUseTheAgreedResponse() {
            // The sequence already distrusts a single clean leg: a spread above 10% adds a
            // third leg and takes the median. The closing moves must be scaled by that same
            // agreed response - scaling them by the discarded first leg would size every
            // return move from the one measurement the sequence decided not to trust.
            var axis = new StressFakeAxis(
                forwardScale: 0.5,
                firstMoveLossArcmin: 6,
                backlashSequence: new[] { 1.0 });

            var outcome = await Calibrate(axis);

            outcome.RestoredToBaseline.Should().BeTrue();
            axis.PhysicalPositionArcmin.Should().BeApproximately(0, 0.5,
                "the closing moves are sized by the response the measurement agreed on");
        }

        [Test]
        public async Task AnAxisThatDiesAfterTheProbe_FailsHonestly_WithoutNaNArithmetic() {
            // The probe passes on the break-away kick, then the axis stops responding: both
            // clean legs read zero. The spread between two zero legs is not a disagreement,
            // it is a dead axis, and the sequence must reach that verdict instead of
            // computing 0/0 and carrying a NaN into the decisions that follow.
            var axis = new StressFakeAxis(forwardScale: 0.0, stictionArcmin: 0, firstMoveLossArcmin: 0);

            var act = async () => await Calibrate(axis);

            await act.Should().ThrowAsync<InvalidOperationException>(
                "an axis that produced no measurable motion fails the pass rather than reporting a NaN factor");
        }

        [Test]
        public async Task WhenTheForwardLegsGiveNoScale_TheClosingMovesBorrowTheHealthyDirection() {
            // An intermittently grabbing clutch: the probe engages, then the first and third
            // clean legs deliver nothing while the second does. The median of three legs is
            // then exactly zero, so the forward direction offers no scale for the closing
            // moves - while the reverse legs measured cleanly. Refusing to close on those
            // grounds would leave the platform wherever the reverse legs put it, with a
            // warning and no restore attempt, so the closing moves borrow the healthy
            // direction exactly as the verdict derivation borrows it for the factor.
            var axis = new StressFakeAxis(
                forwardScale: 0.5,
                backlashSequence: new[] { 1.0 },
                deadCommands: new[] { 1, 3 });

            var outcome = await Calibrate(axis);

            Math.Abs(axis.PhysicalPositionArcmin).Should().BeLessThan(5,
                "a usable reverse response is enough to bring the platform back near its baseline");
            float.IsNaN(outcome.ForwardRatio).Should().BeTrue("the forward direction produced no measurable response");
        }

        /// <summary>
        /// A solver that returns one unusable sample. A plate solve that reports non-finite
        /// coordinates is a driver or solver defect rather than a mechanical one, but it is
        /// the calibration that turns it into motion: what matters is that no arithmetic
        /// built on it can reach the axis.
        /// </summary>
        private sealed class NaNAtSolveAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly int badSolveIndex;
            private readonly double scale;
            private double physicalPositionArcmin;

            public readonly List<float> CommandedMoves = new();
            public int SolveCount { get; private set; }

            public NaNAtSolveAxis(int badSolveIndex, double scale = 0.5) {
                this.badSolveIndex = badSolveIndex;
                this.scale = scale;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                CommandedMoves.Add(arcmin);
                if (float.IsFinite(arcmin)) { physicalPositionArcmin += arcmin * scale; }
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                var index = SolveCount++;
                var observed = index == badSolveIndex ? double.NaN : physicalPositionArcmin;
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, observed / 60.0, 30.0 + observed / 60.0, 0.0));
            }

            public bool CommandedANonFiniteMove => CommandedMoves.Exists(m => !float.IsFinite(m));
        }

        [TestCase(0, TestName = "NaNSolve_InTheNoisePair")]
        [TestCase(3, TestName = "NaNSolve_DuringTheCleanLegs")]
        [TestCase(8, TestName = "NaNSolve_DuringTheBacklashLeg")]
        [TestCase(11, TestName = "NaNSolve_DuringTheClosingLoop")]
        public async Task ASolveThatReportsNonFiniteCoordinates_NeverReachesTheAxisAsACommand(int badSolveIndex) {
            // Every measured quantity in the sequence descends from a solved sample, and a
            // non-finite one propagates through arithmetic that looks guarded: Math.Max(0, NaN)
            // is NaN, and every comparison against NaN is false, so each guard takes its
            // "no" branch and the value travels on. The invariant that has to hold whatever
            // the arithmetic does is at the boundary: the axis must never be commanded to
            // move by a quantity that is not a number.
            var axis = new NaNAtSolveAxis(badSolveIndex);

            try {
                await Calibrate2(axis);
            } catch (InvalidOperationException) {
                // Failing the pass is the right answer; commanding NaN is not.
            }

            axis.CommandedANonFiniteMove.Should().BeFalse(
                "a non-finite measurement must fail the pass, not be sent to the hardware");
        }

        private static Task<AxisCalibrationOutcome> Calibrate2(NaNAtSolveAxis axis) {
            var service = new OapaCalibrationService(axis, axis);
            return service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);
        }

        [Test]
        public async Task AnAxisWhoseBreakAwayCommandCannotFitTheTravelBudget_SaysSoBeforeMoving() {
            // Break-away friction at 50' of command on an axis whose response is healthy: the
            // probe has to reach 135' to move it at all, the legs may not be smaller than that,
            // and three same-direction moves of 135' of sky need 405' of travel against a 180'
            // budget. The sequence cannot measure this rig - what matters is that it says why
            // and stops before spending the travel, instead of aborting halfway through
            // blaming a sky excursion the mechanism never ran away with.
            var axis = new StressFakeAxis(forwardScale: 1.0, stictionArcmin: 50);

            var act = async () => await Calibrate(axis);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*break-away friction*too large*");
            axis.CommandedMoves.Should().HaveCount(5,
                "the four escalating probes, then the restore taking the axis back - not one measuring leg");
            axis.CommandedMoves[4].Should().BeNegative("the only move after the abort is the one bringing the platform home");
            axis.LargestCommand.Should().Be(135f, "the last probe is the largest thing this rig was asked to do");
            Math.Abs(axis.PhysicalPositionArcmin).Should().BeLessThan(1,
                "aborting early is only an improvement if the platform does not stay where the probe left it");
        }

        [Test]
        public async Task AnAxisThatMissesOneLeg_IsNotDiagnosedAsWiredBackwards() {
            // A clutch that grabs intermittently: the first clean leg delivers nothing. The
            // sign of no motion matches no commanded sign, so the direction verdict used to
            // read "inverted" - and the remedy for that is an entire second pass with Reverse
            // flipped, doubling the time and the travel to apply a fix for a fault the rig
            // does not have. Below the detection threshold there is no direction to judge.
            var axis = new StressFakeAxis(
                forwardScale: 0.5,
                backlashSequence: new[] { 1.0 },
                deadCommands: new[] { 1 });

            var outcome = await Calibrate(axis);

            axis.PassCount.Should().Be(1, "a leg that measured nothing is not evidence of inverted wiring");
            outcome.Flipped.Should().BeFalse();
        }

        /// <summary>
        /// Delegates to a real solver until the given solve, then fails every one after it:
        /// clouds arriving mid-calibration, or the camera dropping off the bus. The measured
        /// restore depends on solving, so this is the path that falls back to driving the
        /// commanded sum blind.
        /// </summary>
        private sealed class FailFromSolver : IOapaCalibrationSolver {
            private readonly IOapaCalibrationSolver inner;
            private readonly int failFromIndex;
            private int index;

            public FailFromSolver(IOapaCalibrationSolver inner, int failFromIndex) {
                this.inner = inner;
                this.failFromIndex = failFromIndex;
            }

            public void BeginCalibration() => inner.BeginCalibration();

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                if (index++ >= failFromIndex) {
                    throw new InvalidOperationException("plate solve failed: not enough stars");
                }
                return inner.CaptureAndSolve(token);
            }
        }

        [Test]
        public async Task WhenSolvingDiesAndTheRestoreGoesBlind_ItStillRespectsThePerMoveCap() {
            // Solving stops after the forward legs, so the restore cannot measure anything and
            // falls back to driving the commanded sum. That sum is the total of every leg,
            // several times larger than any single one the sequence allows itself - and this is
            // the one path where nothing is watching the sky while the axis moves. The cap that
            // bounds every measured move has to bound the blind one most of all.
            var axis = new StressFakeAxis(forwardScale: 0.25, stictionArcmin: 50);
            var service = new OapaCalibrationService(axis, new FailFromSolver(axis, failFromIndex: 7));

            var act = async () => await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
            axis.LargestCommand.Should().BeLessThanOrEqualTo(SingleCommandCapArcmin,
                "the blind restore is driven in capped moves, not as one command for the whole sum");
        }

        /// <summary>
        /// Inverted wiring - so the service's flipped second pass is what measures it correctly -
        /// on a rig that seizes after the measuring stages of the first pass and frees itself
        /// again afterwards, a snagged cable or a clutch letting go. The asymmetry between the
        /// two passes is declared rather than derived: what is under test is how the service
        /// composes two passes' worth of physical state, and a rig cannot be talked into
        /// failing one pass and not the other.
        /// </summary>
        private sealed class InvertedRigWithAStuckFirstPass : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly double scale;
            private readonly int seizeFromCommandInFirstPass;
            private int pass;
            private int commandsThisPass;
            private double position;

            public readonly List<float> Commands = new();

            public InvertedRigWithAStuckFirstPass(double scale, int seizeFromCommandInFirstPass) {
                this.scale = scale;
                this.seizeFromCommandInFirstPass = seizeFromCommandInFirstPass;
            }

            public void BeginCalibration() {
                pass++;
                commandsThisPass = 0;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                Commands.Add(arcmin);
                if (pass == 1 && commandsThisPass++ >= seizeFromCommandInFirstPass) { return Task.CompletedTask; }
                position += -1 * arcmin * scale;   // -1: the sky moves against the commanded sign
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) =>
                Task.FromResult(new CalibrationSolveSample(10.0, position / 60.0, 30.0 + position / 60.0, 0.0));

            public double PositionArcmin => position;

            public Task<AxisCalibrationOutcome> Calibrated() =>
                new OapaCalibrationService(this, this).CalibrateAxisWithAutoReverse(
                    Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);
        }

        [Test]
        public async Task WhenOnePassOfTwoDidNotComeHome_TheOutcomeDoesNotClaimItDid() {
            // The auto-flip retry runs the whole sequence twice, and each pass measures its own
            // baseline at its own S0. So a first pass that did not verifiably return home leaves
            // the second one measuring against an already displaced start: the second can close
            // its loop perfectly and still be nowhere near where the user began. Reporting the
            // successful pass's restore alone is the "publishes Done when the axis was not
            // restored" failure wearing a second pass as a disguise.
            var axis = new InvertedRigWithAStuckFirstPass(scale: 0.5, seizeFromCommandInFirstPass: 7);

            var outcome = await axis.Calibrated();

            outcome.Flipped.Should().BeTrue("the rig is wired backwards and the flipped pass measures it correctly");
            outcome.RestoredToBaseline.Should().BeFalse(
                "the first pass never came home, so no later pass can certify the starting position");
        }

        /// <summary>
        /// Normal through the measuring stages, then responding with the opposite sign from the
        /// closing loop onward: what a closing scale with the wrong sign looks like from the
        /// sky. Reachable when the response that scale came from was measured near zero, where
        /// its sign is decided by noise rather than by the mechanism.
        /// </summary>
        private sealed class AxisThatFightsTheClosingMoves : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly double scale;
            private readonly int invertFromCommand;
            private double position;

            public readonly List<float> Commands = new();

            public AxisThatFightsTheClosingMoves(double scale, int invertFromCommand) {
                this.scale = scale;
                this.invertFromCommand = invertFromCommand;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                var sign = Commands.Count >= invertFromCommand ? -1 : +1;
                Commands.Add(arcmin);
                position += sign * arcmin * scale;
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) =>
                Task.FromResult(new CalibrationSolveSample(10.0, position / 60.0, 30.0 + position / 60.0, 0.0));

            public double PositionArcmin => position;
            public int ClosingMoves => Math.Max(0, Commands.Count - invertFromCommand);
        }

        [Test]
        public async Task AClosingMoveThatDrivesTheWrongWay_StopsInsteadOfIterating() {
            // A residual that grew is not the backlash signature - backlash costs a move its
            // travel, it does not spend it in the opposite direction. Growth means the scale
            // driving these moves has the wrong sign, and tolerating it as a stall would spend
            // two more cap-sized moves driving the platform further from where it started.
            var axis = new AxisThatFightsTheClosingMoves(scale: 0.5, invertFromCommand: 7);

            var service = new OapaCalibrationService(axis, axis);
            var outcome = await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);

            axis.ClosingMoves.Should().Be(1, "one move proves the sign is wrong; the rest would only make it worse");
            outcome.RestoredToBaseline.Should().BeFalse();
            Math.Abs(axis.PositionArcmin).Should().BeLessThan(4 * 45,
                "the travel budget bounds the way home as well as the way out");
        }

        [Test]
        public async Task AHighReductionAxisOnTheFactoryFactor_IsToldAboutItsFactor_NotItsClutch() {
            // The first calibration anyone ever runs: the factor is still the factory default
            // of 1, so a logical arcminute is one controller unit and the sky barely moves. On
            // a 1000:1 axis the largest probe covers 0.14' against a 0.35' threshold and the
            // pass fails - and it used to fail blaming the clutch and the motor current on a
            // rig that is mechanically perfect. The evidence to say otherwise is in hand: the
            // probe moved something, just not enough, and how much names the factor.
            var axis = new StressFakeAxis(forwardScale: 1.0 / 970.0);

            var act = async () => await Calibrate(axis, currentRatio: 1f);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*calibration factor far below the truth*970 units per arcminute*against the 1 configured*");
        }

        [Test]
        public async Task AFactorFarAboveTheTruth_IsNotDescribedAsAnAxisThatCannotMoveFreely() {
            // The mirror image, and the one move in the whole sequence sized from no
            // measurement at all: the first probe asks for 5 logical arcminutes, which is
            // whatever the configured factor says it is. Set forty times too high, that single
            // command spends the entire travel budget - and telling this user to check that
            // the axis moves freely points them at the one thing that is working.
            var axis = new StressFakeAxis(forwardScale: 40.0);

            var act = async () => await Calibrate(axis, currentRatio: 100f);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*factor far above the truth*");
        }

        [Test]
        public async Task AFieldThatMovesWhileTheAxisRests_IsNotBlamedOnTheAxis() {
            // A mount that is not tracking drifts about 15' a minute, so two solves taken
            // seconds apart with the axis at rest disagree by arcminutes. S0 has no way to
            // tell that from solve noise: it inflates the detection threshold five-fold, and
            // an axis whose commands were already modest then cannot clear it - so the pass
            // ends up blaming a clutch for the sky turning.
            var axis = new StressFakeAxis(forwardScale: 0.05, driftPerSolveArcmin: 2.5);

            var act = async () => await Calibrate(axis);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage("*mount is tracking*settled*");
        }

        [Test]
        public async Task AFactorMeasuredOnAFractionOfTheIntendedSignal_IsReportedAsProvisional() {
            // The other half of the factory-factor story: on a 262:1 axis the probe does clear
            // the threshold, so the pass succeeds - but the legs are pinned at the command cap
            // and cover half an arcminute of sky where they aim for eight. Both directions
            // agree with each other, so no suspect flag fires; they agree on a number measured
            // a hair above the noise. The factor is usable and worth applying - and worth
            // measuring again once it is in place, which is what the flag is for.
            var axis = new StressFakeAxis(forwardScale: 1.0 / 262.52);

            var outcome = await Calibrate(axis, currentRatio: 1f);

            outcome.Ratio.Should().BeApproximately(262f, 40f, "the factor is recovered, just on little signal");
            outcome.FactorProvisional.Should().BeTrue();
            outcome.ResponseSuspect.Should().BeFalse("the two directions agree - which is exactly why nothing else notices");
        }

        [Test]
        public async Task AFactorMeasuredOnTheSignalItAimsFor_IsNotProvisional() {
            var axis = new StressFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 1.0 });

            var outcome = await Calibrate(axis);

            outcome.FactorProvisional.Should().BeFalse();
        }

        [TestCase(0f, TestName = "ACalibrationStepOfZero")]
        [TestCase(-45f, TestName = "ANegativeCalibrationStep")]
        [TestCase(float.NaN, TestName = "ACalibrationStepThatIsNotANumber")]
        public void AnUnusableCalibrationStep_IsRefusedAtConstruction(float step) {
            // Every bound in the sequence is expressed in steps: the single-command cap is
            // three of them, the travel budget four. A step of zero caps every leg to zero,
            // so the axis is commanded nothing, nothing moves, and the pass fails blaming the
            // clutch and the motor current - a diagnosis pointing at the hardware for a
            // setting the caller passed in.
            var axis = new StressFakeAxis(forwardScale: 1.0);

            var act = () => new OapaCalibrationService(axis, axis, step);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public async Task OnAReversedAxis_AFailedPassIsNotFollowedByARunaway() {
            // The second half of the same fault, and the dangerous half. The restore drives
            // -residual through a response, and on an axis that answers a positive command
            // with a negative displacement - reversed wiring, which is exactly the rig the
            // auto-flip retry exists for - an unsigned response points that move the wrong
            // way. It is not a restore that merely fails to arrive: each iteration then
            // measures a LARGER residual and commands a LARGER move, so the axis accelerates
            // away. Found by holding the failure path to the same physical promises as the
            // successful one, on rig 73 of the randomised sweep: the pass aborted correctly at
            // 185', and the three restore moves that followed - 16', 31', 61' - carried the
            // axis out to 1355'.
            // Rig 112 of that sweep, reproduced exactly rather than approximated: reversed
            // wiring, ten arcminutes of sky per unit forward and half that in reverse.
            var axis = new StressFakeAxis(forwardScale: 10.0, reverseScale: 5.212036683322879,
                                          backlashSequence: new[] { 6.0 }, noiseAmplitudeArcmin: 0.05,
                                          physicalSign: -1);

            var act = async () => await Calibrate(axis);

            await act.Should().ThrowAsync<InvalidOperationException>();
            AssertPhysicalPromises(axis, "reversed axis, scale 10, play 6");
        }

        [Test]
        public async Task WhenTheBudgetAbortsAPass_TheRestoreDoesNotSpendTheBudgetItJustEnforced() {
            // The regression this guards, found by measuring the excursion on the sky instead
            // of on the commanded sum. The residual is measured in sky arcminutes and the axis
            // takes commands in units; the restore used to command the residual itself, which
            // assumes one arcminute of sky per unit - the assumption the whole sequence exists
            // to replace. On this rig, ten arcminutes of sky per unit with 45' of play, the
            // pass aborts 190' from its baseline, and that restore then commanded 135' back,
            // delivered 1350', and left the platform 1115' out on the far side: six times the
            // budget the abort had just enforced, spent by the move meant to protect it.
            var axis = new StressFakeAxis(forwardScale: 10.0, backlashSequence: new[] { 45.0 },
                                          noiseAmplitudeArcmin: 0.05);

            var act = async () => await Calibrate(axis);

            await act.Should().ThrowAsync<Exception>("this rig cannot be measured inside the travel budget");
            axis.PeakSkyExcursionArcmin.Should().BeLessThan(TravelBudgetArcmin + axis.LargestDeliveredStepArcmin,
                "the budget is checked after each move, so the excursion may exceed it by the "
                + "one move that revealed the overshoot - and by nothing that happens after");
            Math.Abs(axis.PhysicalPositionArcmin).Should().BeLessThan(TravelBudgetArcmin,
                "an abort that leaves the axis outside the budget has protected nothing");
        }

        [Test]
        public async Task ASwallowedCommandInOneDirection_DoesNotHalveThatDirectionsResponse() {
            // Found by the randomised sweep below, one rig in 240. A clutch that lets go for a
            // single command - the 04/08 rig did exactly this - can land that command inside
            // one of the two reverse measuring legs. A mean of a full leg and a swallowed one
            // reads half the truth.
            //
            // This test used to assert the wrong answer as expected behaviour, on the grounds
            // that the remedy was a constant in the verdict derivation that had already been
            // merged. That reasoning was wrong: it assumed the only cure was to widen the
            // alarm. The measurement itself was the thing at fault, and it is measured here.
            // The reverse legs now get the same disagreement check and third-leg median the
            // forward legs have always had, from the same function.
            //
            // Nothing had said anything, either. The two directions end up disagreeing by
            // about 1.6, the suspect flag needs a factor of two, and the pass reported a
            // confident number a quarter wrong - which then scaled every correction of the
            // night. Widening the alarm would have printed a warning beside a factor that was
            // still wrong; this returns the right factor instead.
            var axis = new StressFakeAxis(
                forwardScale: 0.5, reverseScale: 0.626,
                backlashSequence: new[] { 45.0 },
                noiseAmplitudeArcmin: 0.02,
                deadCommands: new[] { 9 });

            var outcome = await Calibrate(axis);

            // The claim is not a number this run happened to produce, it is an interval the
            // mechanism defines: the two directional truths are 100/0.626 = 160 and
            // 100/0.5 = 200, so any honest single factor lies between them. The swallowed leg
            // used to put it at 245.7 - outside the interval, on the far side of the slower
            // direction, which is the signature of a measurement halved rather than a mechanism
            // misjudged.
            outcome.Ratio.Should().BeInRange(160f, 200f,
                "a factor outside the two directional truths cannot have come from measuring this mechanism");

            // And the flags stay quiet honestly rather than by luck. With the reverse response
            // measured properly the two directions really do differ by only 1.25, which is
            // ordinary - 1.10 on the 08/08 rig, 1.03 on the 18/08 one, 1.02 on the 04/08 one.
            outcome.ResponseSuspect.Should().BeFalse(
                "the directions now differ by 1.25, which is what this mechanism actually does");
            outcome.Consistent.Should().BeTrue("the directions agree on sign");
        }

        // ===== Hundreds of synthetic mechanisms, not five chosen ones =====
        //
        // Every other test in this file proves a case somebody thought of. These two prove the
        // absence of a regime nobody thought of, which is a different question and the one that
        // has twice caught something in this project that hand-written scenarios had missed.
        //
        // The split is the point. On a mechanism that behaves predictably the sequence is held
        // to ACCURACY: the factor it reports must be the truth. On a mechanism that does not -
        // stick-slip, a clutch that lets go, break-away friction, a drifting field, reversed
        // wiring - accuracy is not always attainable, so it is held to HONESTY instead: bounded
        // commands, bounded travel, no arithmetic that is not a number, and never a claim of
        // having come home that is not true.

        private static IEnumerable<(double scale, double backlash, double noise)> PredictableRigs() {
            foreach (var scale in new[] { 0.1, 0.25, 0.5, 1.0, 2.0, 4.0, 10.0 })
            foreach (var backlash in new[] { 0.0, 0.5, 2.0, 6.0, 20.0, 45.0 })
            foreach (var noise in new[] { 0.0, 0.02, 0.05 }) {
                yield return (scale, backlash, noise);
            }
        }

        /// <summary>
        /// Travel budget, in the only currency the sequence controls. A stored factor that is
        /// ten times too small makes the platform travel ten times further than any command
        /// intends, and no bound expressed in sky arcminutes can prevent that - discovering the
        /// factor is what the first probe is for. So the promise is about commanded travel.
        /// </summary>
        private const float TravelBudgetArcmin = 4f * 45f;

        [Test]
        public async Task OnMechanicallyPredictableRigs_TheFactorItReportsIsTheTruth() {
            var worstError = 0.0;
            var failures = 0;

            foreach (var (scale, backlash, noise) in PredictableRigs()) {
                var axis = new StressFakeAxis(forwardScale: scale,
                    backlashSequence: new[] { backlash }, noiseAmplitudeArcmin: noise);
                AxisCalibrationOutcome outcome;
                try {
                    outcome = await Calibrate(axis);
                } catch (InvalidOperationException) {
                    // Giving up on the measurement is allowed; giving up on the platform is not.
                    AssertPhysicalPromises(axis, $"scale {scale}, play {backlash}, noise {noise} (pass failed)");
                    failures++;
                    continue;
                }

                var truth = 100.0 / scale;
                worstError = Math.Max(worstError, Math.Abs(outcome.Ratio - truth) / truth);

                // A mechanism that behaves keeps the sequence inside its own travel budget:
                // the worst of these 126 leaves the axis 98' from where it started.
                AssertKeptItsPromises(axis, outcome, scale, backlash, noise, skyIsStill: true);
            }

            TestContext.WriteLine($"worst factor error {worstError * 100:F2}%, {failures} honest failures");
            worstError.Should().BeLessThan(0.01,
                "on a mechanism that behaves the same in both directions the measurement is the answer, not an estimate");
            failures.Should().BeLessThan(5, "a predictable rig should rarely defeat the sequence");
        }

        [Test]
        public async Task OnUnpredictableRigs_ItIsEitherRightOrHonest() {
            var rng = new Random(20260819);
            var failures = 0;

            for (var i = 0; i < 240; i++) {
                var scale = new[] { 0.1, 0.25, 0.5, 1.0, 2.0, 4.0, 10.0 }[rng.Next(7)];
                var asym = 1.0 + (rng.NextDouble() - 0.5);
                var play = new[] { 0.0, 0.5, 2.0, 6.0, 20.0, 45.0 }[rng.Next(6)];
                var slip = rng.NextDouble() < 0.4
                    ? new[] { play, play * rng.NextDouble() * 2, play * 0.2 }
                    : new[] { play };
                var noise = new[] { 0.0, 0.02, 0.05, 0.12 }[rng.Next(4)];
                var stiction = rng.NextDouble() < 0.3 ? rng.NextDouble() * 4 : 0;
                var firstLoss = rng.NextDouble() < 0.2 ? rng.NextDouble() * 6 : 0;
                var drift = rng.NextDouble() < 0.25 ? (rng.NextDouble() - 0.5) * 0.4 : 0;
                var sign = rng.NextDouble() < 0.15 ? -1 : 1;
                var az = rng.NextDouble() < 0.3 ? rng.NextDouble() * 50 : 0;
                var dead = rng.NextDouble() < 0.2 ? new[] { rng.Next(2, 10) } : Array.Empty<int>();

                var axis = new StressFakeAxis(scale, scale * asym, slip, stiction, firstLoss,
                    noise, drift, seed: 1000 + i, physicalSign: sign, fieldAzimuthDegrees: az,
                    deadCommands: dead);

                AxisCalibrationOutcome outcome;
                try {
                    outcome = await Calibrate(axis);
                } catch (InvalidOperationException) {
                    // Giving up on the measurement is allowed; giving up on the platform is not.
                    AssertPhysicalPromises(axis, $"rig {i}, scale {scale}, play {play} (pass failed)");
                    failures++;
                    continue;
                }

                AssertKeptItsPromises(axis, outcome, scale, play, noise,
                    skyIsStill: drift == 0, azimuth: az);
            }

            TestContext.WriteLine($"{failures} of 240 refused to report a factor");
            failures.Should().BeGreaterThan(0, "some of these mechanics genuinely cannot be measured");
            failures.Should().BeLessThan(60, "refusing most of them would make the sequence useless");
        }

        /// <summary>
        /// The promises about the platform itself, which hold whether the pass reports a
        /// factor or gives up. They are stated separately because a pass that fails is where
        /// the axis is most at risk, and holding only the successful passes to them leaves
        /// that path unwatched: the restore that used to fling the axis 1115' - six times the
        /// budget - ran exclusively there, and every sweep stayed green through it.
        /// </summary>
        private static void AssertPhysicalPromises(StressFakeAxis axis, string rig) {
            axis.LargestCommand.Should().BeLessThanOrEqualTo(SingleCommandCapArcmin, rig);

            // Measured on the sky, which is where the budget is enforced and where the user's
            // mount actually is. The commanded sum is not the same quantity and cannot stand
            // in for it: on an axis whose factor is wrong the two differ by that factor, so a
            // bound on commanded arcminutes says nothing about how far the platform travelled.
            // The budget is checked after each move, so the excursion may exceed it by the one
            // move that revealed the overshoot - and by nothing that happens afterwards.
            axis.PeakSkyExcursionArcmin.Should().BeLessThanOrEqualTo(
                TravelBudgetArcmin + axis.LargestDeliveredStepArcmin, rig);

            Math.Abs(axis.PhysicalPositionArcmin).Should().BeLessThanOrEqualTo(TravelBudgetArcmin,
                $"a sequence that ends leaving the axis outside its travel budget has protected nothing ({rig})");
        }

        /// <summary>The promises that hold whatever the mechanism looks like.</summary>
        private static void AssertKeptItsPromises(StressFakeAxis axis, AxisCalibrationOutcome outcome,
                double scale, double backlash, double noise, bool skyIsStill, double azimuth = 0) {
            var rig = $"scale {scale}, play {backlash}, noise {noise}";

            AssertPhysicalPromises(axis, rig);

            float.IsNaN(outcome.Ratio).Should().BeFalse(rig);
            float.IsNaN(outcome.BacklashArcmin).Should().BeFalse(rig);
            float.IsNaN(outcome.BacklashEnteringPositiveArcmin).Should().BeFalse(rig);
            float.IsNaN(outcome.BacklashEnteringNegativeArcmin).Should().BeFalse(rig);
            outcome.Ratio.Should().BePositive(rig);

            if (outcome.RestoredToBaseline && skyIsStill) {
                // Only meaningful against a still sky: when the field itself drifts between
                // solves, the sequence closes against a baseline that has moved, and the claim
                // is true of everything it can see. What it cannot see is not a lie.
                var visibleResidual = Math.Abs(axis.PhysicalPositionArcmin * Math.Cos(azimuth * Math.PI / 180.0));

                // One tolerance per pass, because each pass closes against the baseline it
                // measured for itself, and an auto-flip retry starts from wherever the first
                // pass left the axis rather than from where the user did. Two honest closes of
                // 0.34' each leave the platform 0.62' from its starting point, and the claim
                // is still true of every baseline it was ever measured against. Bounding this
                // by a single tolerance asks the sequence for something it never promised -
                // the earlier version of this assertion did, and only fresh rig populations
                // ever showed it.
                visibleResidual.Should().BeLessThan(RestoreToleranceArcmin * axis.PassCount + noise + 0.01,
                    $"claiming restoration means the axis really is back at the baseline of each pass ({rig}, {axis.PassCount} passes)");
            }
        }

        [TestCase(0.25, 20.0, TestName = "StressMatrix_QuarterResponse_LargePlay")]
        [TestCase(4.0, 0.5, TestName = "StressMatrix_FourfoldResponse_SmallPlay")]
        [TestCase(0.1, 6.0, TestName = "StressMatrix_TenthResponse_MediumPlay")]
        [TestCase(1.0, 45.0, TestName = "StressMatrix_UnitResponse_PlayLargerThanTheLeg")]
        public async Task AcrossPlausibleRigs_TheSequenceKeepsItsPhysicalPromises(double scale, double backlash) {
            // The invariants that must hold whatever the mechanics look like: no single
            // command exceeds the cap, the platform never travels past the sky budget, and
            // the axis either verifiably comes home or says it did not.
            const double noise = 0.05;
            var axis = new StressFakeAxis(
                forwardScale: scale,
                backlashSequence: new[] { backlash },
                noiseAmplitudeArcmin: noise);

            var outcome = await Calibrate(axis);

            axis.LargestCommand.Should().BeLessThanOrEqualTo(SingleCommandCapArcmin);
            Math.Abs(axis.PhysicalPositionArcmin).Should().BeLessThan(4 * 45,
                "the travel budget bounds the excursion from the baseline");
            if (outcome.RestoredToBaseline) {
                // The claim can only be as good as the measurement behind it: the residual is
                // judged on solves that carry noise, so "verifiably home" means home to within
                // the restore tolerance plus that noise - not to within the tolerance alone.
                Math.Abs(axis.PhysicalPositionArcmin).Should().BeLessThan(0.5 + noise,
                    "claiming restoration means the axis really is back at its baseline");
            }
        }

        [Test]
        public async Task OnAnInvertedAxis_ASwallowedFirstLeg_DoesNotHideTheInversion() {
            // An axis wired backwards is found by watching which way it goes when it is told to
            // go forward, and the first clean leg is what does the watching. A leg that
            // delivered nothing is not evidence of inversion - the axis went nowhere, and
            // nowhere has no direction - so a swallowed first leg has always been allowed to
            // stand down rather than order a whole second pass with the direction flipped.
            //
            // Standing down was written as "consistent", which is a different claim. It says
            // the axis was watched and behaved, and it stopped the later legs from being
            // consulted at all. So an inverted axis whose first leg happens to be swallowed
            // walks out of the sequence declared correctly wired, on the strength of a leg that
            // measured nothing, while the two legs after it went the other way in plain sight.
            //
            // Nothing catches it downstream. The response is perfectly usable - the median of
            // the later legs is the right number - so no flag fires, and every correction of
            // the night is then applied in the wrong direction on an axis the panel says is
            // fine.
            //
            // This mechanism is inverted, and its first clean leg is the one command the clutch
            // swallows. The two legs after it move, and they move the wrong way.
            var axis = new StressFakeAxis(forwardScale: 1.0, physicalSign: -1,
                                          backlashSequence: new[] { 2.0 },
                                          noiseAmplitudeArcmin: 0.0,
                                          deadCommands: new[] { 1 });

            var outcome = await Calibrate(axis);

            outcome.Flipped.Should().BeTrue(
                "two legs went the wrong way in full view, and a leg that measured nothing does not outvote them");
            axis.PassCount.Should().Be(2, "finding the inversion means running the pass again with the flag flipped");
            outcome.Consistent.Should().BeTrue("the second pass, with the direction flipped, agrees with its commands");
        }

        [Test]
        public async Task OnACorrectlyWiredAxis_ASwallowedFirstLeg_DoesNotOrderASecondPass() {
            // The other half of the rule above, and the reason it was written as it was. The
            // remedy for an inverted axis is a whole second pass with the direction flipped,
            // which doubles the calibration and moves the platform twice as far. Ordering that
            // because one command went missing on a perfectly ordinary axis would be a cure
            // considerably worse than the disease.
            //
            // Same mechanism as above, same swallowed command, wired the right way round. The
            // legs that moved say so, and they are the ones asked.
            var axis = new StressFakeAxis(forwardScale: 1.0,
                                          backlashSequence: new[] { 2.0 },
                                          noiseAmplitudeArcmin: 0.0,
                                          deadCommands: new[] { 1 });

            var outcome = await Calibrate(axis);

            outcome.Flipped.Should().BeFalse("nothing here is wired backwards");
            axis.PassCount.Should().Be(1, "a missing command is not a reason to calibrate the axis twice");
            outcome.Consistent.Should().BeTrue();
        }
    }
}
