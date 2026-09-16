using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The four OAPA backlash modes through the production nudge paths, and the
    /// mode recommendation applied together with the calibration result.
    /// </summary>
    public class OapaBacklashModeVmTest {

        private sealed class FakeSystem : IPolarAlignmentSystem {
            public readonly List<(Axis axis, float move)> RelativeMoves = new();

            public bool Connected => true;
            public string Status => "Idle";
            public float XPosition1 => 0;
            public float YPosition1 => 0;
            public float ZPosition1 => 0;
            public float XGearRatio { get; set; } = 1;
            public float YGearRatio { get; set; } = 1;
            public float ZGearRatio { get; set; } = 1;
            public LastDirection XLastDirection { get; private set; } = LastDirection.Positive;
            public LastDirection YLastDirection { get; private set; } = LastDirection.Positive;
            public LastDirection ZLastDirection { get; private set; } = LastDirection.Positive;

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
                RelativeMoves.Add((axis, position));
                var direction = position >= 0 ? LastDirection.Positive : LastDirection.Negative;
                switch (axis) {
                    case Axis.XAxis: XLastDirection = direction; break;
                    case Axis.YAxis: YLastDirection = direction; break;
                    case Axis.ZAxis: ZLastDirection = direction; break;
                }
                return Task.CompletedTask;
            }

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) => Task.CompletedTask;
            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }
        }

        private static (UniversalPolarAlignmentOAPAVM vm, FakeSystem system) Vm(OapaBacklashMode yMode, float yCompensation = 8f) {
            var system = new FakeSystem();
            var vm = new OapaTestVm { Hardware = system };
            vm.ReverseAltitude = false;
            vm.YBacklashCompensation = yCompensation;
            // Symmetric by construction: these tests are about the modes, not about
            // direction-dependent play, and the setting is process-global.
            vm.YBacklashCompensationNegative = yCompensation;
            vm.YBacklashMode = yMode;
            return (vm, system);
        }

        private static async Task<FakeSystem> Reversal(UniversalPolarAlignmentOAPAVM vm, FakeSystem system) {
            (await vm.TryNudgeY(15f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();
            (await vm.TryNudgeY(-10f, CancellationToken.None)).Should().BeTrue();
            return system;
        }

        [Test]
        public async Task Off_Reversal_PlainMove() {
            var (vm, system) = Vm(OapaBacklashMode.Off);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal((Axis.YAxis, -10f));
        }

        [Test]
        public async Task Soft_Reversal_ThreeQuartersFoldedIn() {
            var (vm, system) = Vm(OapaBacklashMode.Soft);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal((Axis.YAxis, -16f));
        }

        [Test]
        public async Task Full_Reversal_WholeBacklashFoldedIn() {
            var (vm, system) = Vm(OapaBacklashMode.Full);
            await Reversal(vm, system);
            system.RelativeMoves.Should().Equal((Axis.YAxis, -18f));
        }

        [Test]
        public async Task Unidirectional_Reversal_OvershootsAndReturns() {
            var (vm, system) = Vm(OapaBacklashMode.Unidirectional);
            await Reversal(vm, system);
            // Overshoot = B + (0.25*B + 0.5') = 10.5: out to -(10+10.5), back +10.5.
            system.RelativeMoves.Should().Equal((Axis.YAxis, -20.5f), (Axis.YAxis, 10.5f));
        }

        /// <summary>
        /// Drives the axis negative so the one-sided clearing has something to do, then runs an
        /// absolute move and returns only what the clearing emitted.
        /// </summary>
        private static async Task<List<(Axis axis, float move)>> ClearingAfterAbsoluteMove(
            UniversalPolarAlignmentOAPAVM vm, FakeSystem system) {

            (await vm.TryNudgeY(-10f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();
            vm.TargetPositionY = -20f;
            await vm.MoveY(CancellationToken.None);
            return system.RelativeMoves;
        }

        [Test]
        public async Task Off_AbsoluteMove_CompensatesNothing() {
            // Field report (17/08): with the mode on Off, every "move to" was still followed by
            // two extra moves of the full backlash - 34.82' on that rig, several turns of the
            // adjustment knob. The modes only governed the relative nudges; the absolute path
            // asked for a compensation value and got the raw setting.
            //
            // It is not only an annoyance: it makes measuring one's own backlash by hand
            // impossible, because the only tool available for the measurement perturbs the
            // thing being measured.
            var (vm, system) = Vm(OapaBacklashMode.Off);

            var clearing = await ClearingAfterAbsoluteMove(vm, system);

            clearing.Should().BeEmpty("Off has to mean off on every path that moves the axis");
        }

        [Test]
        public async Task Full_AbsoluteMove_StillCompensates() {
            // The other half of the same claim: silencing Off must not silence the modes that
            // are supposed to compensate.
            var (vm, system) = Vm(OapaBacklashMode.Full);

            var clearing = await ClearingAfterAbsoluteMove(vm, system);

            clearing.Should().NotBeEmpty("a configured mode still clears the play after an absolute move");
        }

        [Test]
        public void AReversalFinerThanTheMeasurement_IsNotCommanded() {
            // Field case (16/08): the correction loop asked the altitude axis for 0.19' with a
            // 0.68' compensation configured. The plan came out at 0.87', the error tripled,
            // and the same thing happened four times before the alignment gave up at 0.38' -
            // where a fresh measurement then found 17'34". A reversal finer than the play was
            // measured to cannot be honoured: compensating overshoots it, not compensating
            // loses it to the play.
            var plan = BacklashModePlanner.PlanMoves(OapaBacklashMode.Full, -0.19f, 2.19f, 0.68f,
                LastDirection.Positive, minimumReversalArcmin: 0.25f);

            plan.Should().BeEmpty();
        }

        [Test]
        public void TheSameRequestInTheEngagedDirection_IsCommandedInFull() {
            // Only reversals pay the play. An axis continuing the way it was already going
            // arrives exactly at any size, which is why a clean axis keeps converging as
            // finely as the solver can measure.
            var plan = BacklashModePlanner.PlanMoves(OapaBacklashMode.Full, 0.19f, 2.19f, 0.68f,
                LastDirection.Positive, minimumReversalArcmin: 0.25f);

            plan.Should().Equal(0.19f);
        }

        [Test]
        public void WithoutAMeasuredFloor_SmallReversalsAreStillPlanned() {
            // The floor is supplied by whoever knows how well the play was measured, and is
            // absent by default: the field replay suite shows that under a mechanism losing
            // exactly its configured play, small reversals land exactly and are needed to
            // converge. Their size is not what makes them unsafe.
            var plan = BacklashModePlanner.PlanMoves(OapaBacklashMode.Full, -0.19f, 2.19f, 0.68f,
                LastDirection.Positive);

            plan.Should().Equal(-0.19f - 0.68f);
        }

        [Test]
        public async Task SameDirection_AnyMode_PlainMove() {
            var (vm, system) = Vm(OapaBacklashMode.Unidirectional);
            (await vm.TryNudgeY(15f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();
            (await vm.TryNudgeY(5f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Should().Equal((Axis.YAxis, 5f));
        }

        /// <summary>Applies a calibration result with the given Y pair, single-step.</summary>
        private static UniversalPolarAlignmentOAPAVM ApplyYPair(float positive, float negative,
            UniversalPolarAlignmentOAPAVM existing = null) {

            var vm = existing;
            if (vm == null) {
                vm = new OapaTestVm { Hardware = new FakeSystem() };
                Properties.Settings.Default.OAPAXBacklashSource = "Default";
                Properties.Settings.Default.OAPAYBacklashSource = "Default";
                Properties.Settings.Default.OAPAXGearRatioSource = "Default";
                Properties.Settings.Default.OAPAYGearRatioSource = "Default";
                // The remembered split outlives the VM - it is a setting, and settings are
                // process-global in these fixtures. Without clearing it, a test asking for a
                // first-ever calibration inherits the previous test's pass and gets its split
                // confirmed instead of collapsed.
                Properties.Settings.Default.OAPAXBacklashSplitLast = 0f;
                Properties.Settings.Default.OAPAYBacklashSplitLast = 0f;
            }
            vm.DiscoveredXRatio = 100;
            vm.DiscoveredYRatio = 100;
            vm.DiscoveredXBacklash = 1f;
            vm.DiscoveredXBacklashNegative = 1f;
            vm.DiscoveredYBacklash = positive;
            vm.DiscoveredYBacklashNegative = negative;
            vm.DiscoveredXNoise = 0.05f;
            vm.DiscoveredYNoise = 0.05f;
            vm.HasCalibrationResult = true;
            vm.ApplyCalibration();
            return vm;
        }

        [Test]
        public void AFirstDirectionSplit_IsAppliedAsItsMean_UntilASecondPassAgrees() {
            // One pass cannot tell a real asymmetry from a slipped measurement, and the two
            // are not equally cheap to get wrong: the difference between the pair lands as a
            // fixed bias on every reversal, so the axis can no longer be corrected by less
            // than that difference. Two rigs stalled at exactly their configured gap.
            var vm = ApplyYPair(2.19f, 0.68f);

            vm.YBacklashCompensation.Should().BeApproximately(1.435f, 0.01f);
            vm.YBacklashCompensationNegative.Should().BeApproximately(1.435f, 0.01f);
        }

        [Test]
        public void ASplitThatFlipsBetweenCalibrations_IsCollapsed_NotApplied() {
            // Field evidence, one rig, two consecutive nights, same axis: 1.45'/1.96' and then
            // 2.19'/0.68'. The sum barely moved, the larger side changed places. A stable sum
            // with a flipped split is slippage, not mechanics.
            var vm = ApplyYPair(1.45f, 1.96f);
            ApplyYPair(1.50f, 2.10f, vm);          // agrees: the negative side is heavier both times
            vm.YBacklashCompensation.Should().BeApproximately(1.50f, 0.01f, "two passes agreed on which side is heavier");
            vm.YBacklashCompensationNegative.Should().BeApproximately(2.10f, 0.01f);

            ApplyYPair(2.19f, 0.68f, vm);          // flips

            vm.YBacklashCompensation.Should().BeApproximately(1.435f, 0.01f);
            vm.YBacklashCompensationNegative.Should().BeApproximately(1.435f, 0.01f);
        }

        [Test]
        public void ApplyCalibration_SetsTheRecommendedModePerAxis_AndSaysSo() {
            var vm = new OapaTestVm { Hardware = new FakeSystem() };
            vm.XBacklashMode = OapaBacklashMode.Full;
            vm.YBacklashMode = OapaBacklashMode.Full;
            // Arranged values are test fixtures, not manual user edits: keep Apply single-step.
            Properties.Settings.Default.OAPAXGearRatioSource = "Default";
            Properties.Settings.Default.OAPAYGearRatioSource = "Default";
            Properties.Settings.Default.OAPAXBacklashSource = "Default";
            Properties.Settings.Default.OAPAYBacklashSource = "Default";
            vm.DiscoveredXRatio = 100;
            vm.DiscoveredYRatio = 100;
            vm.DiscoveredXBacklash = 0.2f;   // below measurability -> Off
            vm.DiscoveredYBacklash = 20f;    // large -> Unidirectional
            vm.DiscoveredXNoise = 0.05f;
            vm.DiscoveredYNoise = 0.05f;
            vm.HasCalibrationResult = true;

            vm.ApplyCalibration();

            vm.XBacklashMode.Should().Be(OapaBacklashMode.Off);
            vm.YBacklashMode.Should().Be(OapaBacklashMode.Unidirectional);
            vm.CalibrationStatus.Should().Contain("Backlash mode");
            vm.CalibrationStatus.Should().Contain("Unidirectional");
        }

        [Test]
        public void BacklashMode_PersistsAndParsesItsSetting() {
            var vm = new OapaTestVm();

            vm.XBacklashMode = OapaBacklashMode.Unidirectional;
            Properties.Settings.Default.OAPAXBacklashMode.Should().Be("Unidirectional");
            vm.XBacklashMode.Should().Be(OapaBacklashMode.Unidirectional);

            // A corrupted stored value falls back to the default rather than crashing.
            Properties.Settings.Default.OAPAXBacklashMode = "garbage";
            vm.XBacklashMode.Should().Be(OapaBacklashMode.Full);
        }

        [Test]
        public async Task AReversalBelowTheMeasuredFloor_IsNotCommanded_ThroughTheVm() {
            // The floor comes from the noise the calibration measured on that axis, which the
            // pass persists. Below it the compensation's own error exceeds the correction being
            // attempted, so nothing is commanded and the log says why.
            var (vm, system) = Vm(OapaBacklashMode.Full, yCompensation: 2.19f);
            Properties.Settings.Default.OAPAYCalibrationNoise = 0.05f;   // floor = max(5 x 0.05, 0.25) = 0.25'

            (await vm.TryNudgeY(15f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();

            (await vm.TryNudgeY(-0.19f, CancellationToken.None)).Should().BeTrue();

            system.RelativeMoves.Should().BeEmpty("0.19' is finer than the 0.25' this axis was measured to");
        }

        [Test]
        public async Task WithoutAMeasuredNoise_TheSameReversalIsCommanded() {
            // The floor is not a constant: an axis whose calibration never measured the noise
            // keeps converging as finely as the solver allows.
            var (vm, system) = Vm(OapaBacklashMode.Full, yCompensation: 2.19f);
            Properties.Settings.Default.OAPAYCalibrationNoise = 0f;

            (await vm.TryNudgeY(15f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();

            (await vm.TryNudgeY(-0.19f, CancellationToken.None)).Should().BeTrue();

            system.RelativeMoves.Should().Equal((Axis.YAxis, -0.19f - 2.19f));
        }

        // The beta also leaves the play uncompensated inside a band near the target, where the
        // direction of each requested correction comes from estimate jitter rather than from
        // the error. That rule reads the correction ceiling and the error the current run
        // started with, neither of which exists here, so it follows separately with its cases.
    }
}
