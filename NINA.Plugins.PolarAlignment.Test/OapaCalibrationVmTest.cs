using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Drives the calibration through the production VM command with a fake controller
    /// and solver: a slippage verdict must disable Apply with an explanatory message,
    /// and an asymmetry flag must report both directional ratios.
    /// </summary>
    public class OapaCalibrationVmTest {

        /// <summary>
        /// Fake OAPA hardware and solver in one: motion updates per-axis physical positions
        /// (with configurable response scale and per-reversal backlash sequence), solves
        /// report them. Altitude ~0 keeps cos(alt)=1 so measured azimuth equals posX.
        /// </summary>
        private sealed class FakeRig : IPolarAlignmentSystem, IOapaCalibrationSolver {
            private readonly double forwardScale;
            private readonly double reverseScale;
            private readonly double[] backlashSequence;
            private readonly Dictionary<Axis, (double pos, int lastSign, int reversals)> axes = new() {
                [Axis.XAxis] = (0, 0, 0),
                [Axis.YAxis] = (0, 0, 0),
            };

            /// <summary>Wiring: -1 is an axis that moves the sky the opposite way from its command.</summary>
            private readonly int physicalSign;

            public FakeRig(double forwardScale = 1.0, double? reverseScale = null, double[] backlashSequence = null,
                           int physicalSign = 1) {
                this.forwardScale = forwardScale;
                this.reverseScale = reverseScale ?? forwardScale;
                this.backlashSequence = backlashSequence ?? new[] { 0.0 };
                this.physicalSign = physicalSign;
            }

            public bool Connected { get; private set; } = true;
            public string Status => "Idle";
            public float XPosition1 => 0;
            public float YPosition1 => 0;
            public float ZPosition1 => 0;
            public float XGearRatio { get; set; } = 1;
            public float YGearRatio { get; set; } = 1;
            public float ZGearRatio { get; set; } = 1;
            public LastDirection XLastDirection => LastDirection.Positive;
            public LastDirection YLastDirection => LastDirection.Positive;
            public LastDirection ZLastDirection => LastDirection.Positive;

            /// <summary>Command index (1-based) from which the controller stops answering; 0 = never.</summary>
            public int LinkDiesFromCommand;

            private int relativeMoves;

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
                relativeMoves++;
                if (LinkDiesFromCommand > 0 && relativeMoves >= LinkDiesFromCommand) {
                    Connected = false;
                    throw new InvalidOperationException("the controller stopped answering");
                }
                var (pos, lastSign, reversals) = axes[axis];
                var sign = Math.Sign(position);
                var scale = sign >= 0 ? forwardScale : reverseScale;
                double effective = Math.Abs(position) * scale;
                if (sign != 0 && lastSign != 0 && sign != lastSign) {
                    effective = Math.Max(0, effective - backlashSequence[reversals % backlashSequence.Length]);
                    reversals++;
                }
                if (sign != 0) { lastSign = sign; }
                axes[axis] = (pos + physicalSign * sign * effective, lastSign, reversals);
                return Task.CompletedTask;
            }

            /// <summary>Absolute moves, in the order they were commanded.</summary>
            public readonly List<Axis> AbsoluteMoves = new();

            /// <summary>
            /// How many measuring passes were opened. One calibration opens one per axis, plus
            /// one more for each auto-flip retry, so it tells a single run from two overlapping
            /// ones - which the physical positions cannot, since two runs over the same axes
            /// leave the same kind of trace as one.
            /// </summary>
            public int PassCount { get; private set; }

            public void BeginCalibration() => PassCount++;

            /// <summary>An axis whose absolute move fails, as a controller that stops answering does.</summary>
            public Axis? AbsoluteMoveFailsOn;

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
                AbsoluteMoves.Add(axis);
                if (AbsoluteMoveFailsOn == axis) {
                    throw new InvalidOperationException("the controller stopped answering");
                }
                return Task.CompletedTask;
            }
            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, axes[Axis.YAxis].pos / 60.0, axes[Axis.YAxis].pos / 60.0, 0.0 + axes[Axis.XAxis].pos / 60.0));
            }
        }

        private static UniversalPolarAlignmentOAPAVM Vm(FakeRig rig) {
            var vm = new OapaTestVm { Hardware = rig, Solver = rig };
            vm.ReverseAzimuth = false;
            vm.ReverseAltitude = false;
            vm.XGearRatio = 100;
            vm.YGearRatio = 100;
            return vm;
        }

        [Test]
        public async Task AnAutoFlippedDirection_IsNotPersistedUntilApply() {
            // Both axes are wired backwards, so both passes flip and the calibration succeeds
            // on the retry. The flags must not have moved yet: a run is not a decision, Apply
            // is, and every other measured value already waits for it.
            //
            // What used to happen: the flag was written and saved the moment an axis came back
            // flipped, which is halfway through the run. Cancel during the second axis, read
            // "Cancelled", and a saved setting had changed anyway - the one quantity in this
            // panel that moved without anybody pressing anything.
            var vm = Vm(new FakeRig(physicalSign: -1));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.HasCalibrationResult.Should().BeTrue();
            vm.DiscoveredReverseAzimuth.Should().BeTrue("the pass settled on the flipped direction");
            vm.DiscoveredReverseAltitude.Should().BeTrue();
            vm.ReverseAzimuth.Should().BeFalse("nothing is persisted until Apply");
            vm.ReverseAltitude.Should().BeFalse();
            vm.CalibrationConsistencyMessage.Should().Contain("on Apply",
                "a user reading the result has to know the direction is part of what Apply will change");
        }

        [Test]
        public async Task ApplyingAFlippedCalibration_MovesTheDirectionWithTheFactor() {
            // The two cannot be separated. A flipped axis is measured by a second pass run
            // with the direction reversed, so the factor in the outcome is the factor *under
            // that flag*: applying one without the other leaves the axis corrected by a number
            // measured going the other way.
            var vm = Vm(new FakeRig(physicalSign: -1));
            // An edit under any starting state: settings are process-global, so what Vm()
            // stored may or may not have changed anything.
            vm.XGearRatio = vm.XGearRatio + 7f;
            await vm.CalibrateGearRatios(CancellationToken.None);

            // A hand-entered factor makes the first Apply spend itself asking whether to
            // overwrite it. That gate has to hold the direction back too - a flag that slipped
            // through while the factor it was measured with waited for a second press would be
            // the same split state, arriving by a different door.
            vm.ApplyCalibration();
            vm.ApplyConfirmationPending.Should().BeTrue();
            vm.ReverseAzimuth.Should().BeFalse("the confirmation gate holds the direction as well");

            vm.ApplyCalibration();

            vm.ReverseAzimuth.Should().BeTrue("Apply is what commits the direction");
            vm.ReverseAltitude.Should().BeTrue();
            vm.XGearRatioSource.Should().Be(OapaParameterSource.Calibrated,
                "and the factor measured under that direction is committed with it");
            vm.CalibrationStatus.Should().Contain("Reverse Az",
                "the one change that alters which way the axis moves is named where it is applied");
        }

        [Test]
        public async Task DiscardingAFlippedCalibration_LeavesNothingForALaterApply() {
            var vm = Vm(new FakeRig(physicalSign: -1));
            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.DiscardCalibration();

            vm.DiscoveredReverseAzimuth.Should().Be(vm.ReverseAzimuth,
                "a discarded result must not leave a direction change waiting to be picked up");
            vm.DiscoveredReverseAltitude.Should().Be(vm.ReverseAltitude);
        }

        [Test]
        public async Task AMoveHomeThatStopsBetweenTheAxes_SaysWhichOneMadeIt() {
            // The axes are driven one after the other, so a failure on the second leaves the
            // platform at home in azimuth and wherever it stood in altitude. "Failed to move to
            // home position" describes that state and the state where neither axis moved
            // equally well, and they are not the same thing to somebody standing at the mount.
            var rig = new FakeRig { AbsoluteMoveFailsOn = Axis.YAxis };
            var vm = Vm(rig);
            await vm.SetHome(CancellationToken.None);

            await vm.GoHome(CancellationToken.None);

            rig.AbsoluteMoves.Should().Equal(new[] { Axis.XAxis, Axis.YAxis },
                "azimuth is commanded first, and altitude is still attempted");
            vm.IsNotMoving.Should().BeTrue("a failed move must not leave the panel thinking the axis is still going");

            // What this does NOT prove, and it is worth saying rather than leaving the two
            // assertions below looking stronger than they are: that GoHome puts this sentence
            // in front of anybody. The failure is reported through a toast and the log, and
            // neither is observable from here - so the wording is pinned, the wiring is not.
            // Making it observable means giving the command a status line of its own, which is
            // a change to the panel rather than to this failure path.
            UniversalPolarAlignmentOAPAVM.Arrived(true).Should().Contain("azimuth reached home");
            UniversalPolarAlignmentOAPAVM.Arrived(false).Should().Contain("neither");
        }

        // Every stage of a pass, rather than one chosen moment: the probe, the clean legs, the
        // reversal and its escalation, the reverse legs, and the closing loop. A single index
        // proves the one branch it happens to land in, and the branch that matters most here -
        // the restore trying to undo a pass with nothing left to move the axis with - only
        // opens on the later ones.
        [TestCase(1, TestName = "the first probe")]
        [TestCase(4, TestName = "the clean legs")]
        [TestCase(7, TestName = "the reversal")]
        [TestCase(11, TestName = "the reverse legs")]
        [TestCase(15, TestName = "the closing loop")]
        public async Task TheControllerGoingAwayMidPass_LeavesThePanelUsableAndOffersNoResult(int diesAt) {
            // A cable pulled, a USB port that resets, a controller that browns out: every move
            // from that point on throws, and so does the restore that tries to undo the pass -
            // there is no longer anything to move the axis with. The measurement is lost and
            // that is fine; what must not be lost is the panel.
            //
            // Two ways this goes wrong and neither is visible from reading the happy path. An
            // exception that escapes leaves CalibrationRunning standing, and the Calibrate
            // button never comes back for the rest of the session - the user's remedy is to
            // restart N.I.N.A. And a result left over from the pass would put a factor derived
            // from a half-finished measurement one click away from being applied.
            var rig = new FakeRig { LinkDiesFromCommand = diesAt };
            var vm = Vm(rig);

            await vm.CalibrateGearRatios(CancellationToken.None);

            rig.Connected.Should().BeFalse("the premise of this test is a controller that went away");
            vm.CalibrationRunning.Should().BeFalse("the panel must not be left believing a pass is in progress");
            vm.IsNotMoving.Should().BeTrue("nor that the axis is still moving");
            vm.CanCalibrate().Should().BeFalse("with the controller gone, but for the connection - not for a stuck flag");
            vm.HasCalibrationResult.Should().BeFalse(
                "a pass that died partway measured nothing worth applying");
            vm.CalibrationStatus.Should().StartWith("Failed", "and it has to say so rather than look idle");
        }

        [Test]
        public async Task TwoRequestsInARow_DoNotRunTwoCalibrationsOverTheSameAxes() {
            // The command hands back a task straight away and does its work on a worker, so a
            // flag raised inside that work is raised some time after the click. Until then the
            // button is still live. Two presses used to start two sequences over the same two
            // axes, each one measuring displacements the other was causing, and both of them
            // commanding the mount.
            //
            // The camera check that stands at the top is not a guard against this: it is asked
            // with this panel's own token, which this panel's own block does not make busy, and
            // it answers yes outright when there is no camera mediator - which is the case here
            // and on any rig where one is not wired in.
            var rig = new FakeRig();
            var vm = Vm(rig);

            var first = vm.CalibrateGearRatios(CancellationToken.None);
            var second = vm.CalibrateGearRatios(CancellationToken.None);
            await Task.WhenAll(first, second);

            rig.PassCount.Should().Be(2,
                "one calibration is one pass per axis; a second request must not add two more");
            vm.CalibrationRunning.Should().BeFalse("and the panel must not be left claiming a run is in progress");
            vm.IsNotMoving.Should().BeTrue();
        }

        [Test]
        public async Task EditingAValueWhileApplyIsAsking_MakesItAskAgain() {
            // The confirmation names the values it is about to overwrite: "X factor 250 ->
            // 300. Press Apply again to confirm." Editing one of them while that is on screen
            // used to leave the pending state standing, so the next press applied without a
            // word - and what it applied was not what the sentence described.
            //
            // The gate exists to protect values somebody entered deliberately. Typing one in
            // while it is asking is the most deliberate act available; it cannot be the act
            // that switches the gate off.
            var vm = Vm(new FakeRig());
            // Settings are process-global, so whether Vm()'s assignment counts as an edit
            // depends on what the previous test left stored. Move the value off whatever it
            // currently is, which is an edit under any starting state.
            vm.XGearRatio = vm.XGearRatio + 7f;
            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.ApplyCalibration();
            vm.ApplyConfirmationPending.Should().BeTrue("the factor was entered by hand");

            vm.XGearRatio = 250f;

            vm.ApplyConfirmationPending.Should().BeFalse(
                "the prompt described values that have just changed, so it no longer describes anything");
            vm.ApplyCalibration();
            vm.ApplyConfirmationPending.Should().BeTrue("and Apply has to ask again, about the new values");
        }

        [Test]
        public void AFirstEverProfile_IsNotToldItsSettingsWereRepaired() {
            // Schema 0 is both "an old profile carrying a direction split to undo" and "a
            // machine that has never run this plugin", because 0 is also the factory value.
            // The repair runs either way and is harmless when there is nothing to repair; the
            // announcement is not. Told that a previous release stored a bad value and that
            // they should re-run the Self-Calibration, a first-time user goes looking for a
            // problem that does not exist and a calibration they have never run.
            const float NotSet = -1f;

            OapaSettingsMigration.HadStoredPair(NotSet, NotSet).Should().BeFalse(
                "a fresh profile already holds the sentinel in both directions - nothing was repaired");
            OapaSettingsMigration.HadStoredPair(2.5f, NotSet).Should().BeTrue();
            OapaSettingsMigration.HadStoredPair(NotSet, 2.5f).Should().BeTrue();
            OapaSettingsMigration.HadStoredPair(0f, NotSet).Should().BeTrue(
                "a stored zero is a measured symmetric axis, not an absent value");
        }

        [Test]
        public void AnApplyThatDiesPartway_NamesWhatItAlreadyWrote() {
            // Apply persists one setting at a time, so it can stop halfway. Saying only
            // "failed" then is not true: a user who reads it concludes their configuration is
            // untouched, and carries on with a direction that changed and a factor that did
            // not - the half-state that makes an axis correct the wrong way.
            //
            // This is a defect introduced by the fix above rather than found in old code. The
            // direction was moved ahead of the factors on purpose, which is the survivable
            // order of the two, and that is exactly what put a persisted write in front of
            // everything else that could still fail.
            UniversalPolarAlignmentOAPAVM.PartialApplyNote(new[] { "Reverse Az", "factors" })
                .Should().Contain("Reverse Az").And.Contain("factors");
            UniversalPolarAlignmentOAPAVM.PartialApplyNote(new[] { "Reverse Az" })
                .Should().Contain("Re-run the calibration", "half a calibration is not a calibration");

            // And nothing written means nothing claimed: an Apply that failed on its first
            // step must not invent a list of things it changed.
            UniversalPolarAlignmentOAPAVM.PartialApplyNote(System.Array.Empty<string>())
                .Should().BeEmpty();
        }

        [Test]
        public async Task DirectionalBacklash_WarnsWithBothFigures_ButKeepsApplyEnabled() {
            // Transitions that cost 20' and 8' are two different quantities, not a broken
            // measurement: withholding Apply over them also withholds the calibration
            // factor, which they do not affect. Warn, report both, and let it through.
            var vm = Vm(new FakeRig(backlashSequence: new[] { 20.0, 8.0 }));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.HasCalibrationResult.Should().BeTrue();
            vm.CalibrationDirectionalBacklash.Should().BeTrue();
            vm.CanApplyCalibration().Should().BeTrue("an imperfect mean compensation is still worth applying");
            vm.CalibrationConsistencyMessage.Should().Contain("different amount in each direction");
            vm.CalibrationConsistencyMessage.Should().Contain("vs", "both transitions must be shown, not just their mean");
            vm.CalibrationConsistencyMessage.Should().Contain("between calibrations",
                "the warning has to say what would actually prove slipping mechanics");
        }

        [Test]
        public async Task SymmetricBacklash_DoesNotWarn() {
            var vm = Vm(new FakeRig(backlashSequence: new[] { 5.0 }));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.HasCalibrationResult.Should().BeTrue();
            vm.CalibrationDirectionalBacklash.Should().BeFalse();
            vm.CanApplyCalibration().Should().BeTrue();
            vm.CalibrationConsistencyMessage.Should().NotContain("different amount in each direction");
        }

        [Test]
        public async Task AsymmetricAxis_ReportsBothDirectionalRatios() {
            var vm = Vm(new FakeRig(forwardScale: 1.0, reverseScale: 0.8));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.CalibrationConsistencyMessage.Should().Contain("forward");
            vm.CalibrationConsistencyMessage.Should().Contain("reverse");
        }

        // What the panel says when the pass itself was not clean. The verdicts are derived and
        // tested in the calibration service; these pin that each one reaches the user, because
        // a verdict nobody reads is a failure reported as a success.

        [Test]
        public async Task AResponseThatCollapsesInOneDirection_IsNamedAsLostMotion() {
            // One direction answering at less than half the other is not an asymmetry to
            // average: the weaker one is losing steps, and the user has to be pointed at the
            // run current and speed rather than handed a factor as if nothing happened.
            var vm = Vm(new FakeRig(forwardScale: 1.0, reverseScale: 0.3));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.CalibrationConsistencyMessage.Should().Contain("disagree by more than a factor of two");
            vm.CalibrationConsistencyMessage.Should().Contain("run current");
        }

        [Test]
        public async Task AReversalLongerThanTheResponseAllows_IsReportedAsUnmeasured_NotAsAValue() {
            // A reversal that travels further than the clean response predicts cannot be play.
            // The pass reports zero for it, and the message has to say that zero means "could
            // not be measured" - otherwise it reads as a clean axis with no backlash.
            var vm = Vm(new FakeRig(backlashSequence: new[] { -6.0 }));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.CalibrationConsistencyMessage.Should().Contain("could not be measured");
        }

        [Test]
        public async Task APassThatDoesNotGetBackToItsStart_SaysSo_InTheStatus() {
            // The closing moves are sized by the response the pass measured, and on an axis
            // that answers much less going back they fall short three times in a row. The
            // factors are still valid; what is not true is "Done" on its own, because the
            // platform stays wherever the last closing move left it.
            var vm = Vm(new FakeRig(forwardScale: 1.0, reverseScale: 0.55, backlashSequence: new[] { 5.0 }));

            await vm.CalibrateGearRatios(CancellationToken.None);

            vm.HasCalibrationResult.Should().BeTrue("the measured factors are still offered");
            vm.CalibrationStatus.Should().Contain("not returned to start");
        }
    }
}
