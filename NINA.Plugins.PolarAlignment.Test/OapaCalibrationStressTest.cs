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
            private int lastSign;

            public int SolveCount { get; private set; }
            /// <summary>One per calibration pass, so a test can tell a single pass from an auto-flip retry.</summary>
            public int PassCount { get; private set; }
            public readonly List<float> CommandedMoves = new();

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
            public float LargestCommand => CommandedMoves.Count == 0 ? 0f : CommandedMoves.Max(Math.Abs);
        }

        private static Task<AxisCalibrationOutcome> Calibrate(StressFakeAxis axis, float currentRatio = 100f, bool reversed = false) {
            var service = new OapaCalibrationService(axis, axis);
            return service.CalibrateAxisWithAutoReverse(Axis.YAxis, currentRatio, reversed, "Y", null, CancellationToken.None);
        }

        /// <summary>The single-command cap the sequence applies to its own legs: three calibration steps.</summary>
        private const float SingleCommandCapArcmin = 3f * 45f;

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
    }
}
