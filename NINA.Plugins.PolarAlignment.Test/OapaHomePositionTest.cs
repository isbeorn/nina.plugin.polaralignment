using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The stored home must survive gear-ratio changes: Set Home marks a physical controller
    /// position, so Go Home has to return there no matter which calibration factor is active
    /// when it runs — after manual factor edits as well as after applying a calibration.
    /// </summary>
    public class OapaHomePositionTest {

        private sealed class FakeSystem : IPolarAlignmentSystem {
            public readonly List<(Axis axis, float target)> AbsoluteMoves = new();

            public bool Connected => true;
            public string Status => "Idle";
            public float XPosition1 { get; private set; }
            public float YPosition1 { get; private set; }
            public float ZPosition1 => 0;
            public float XGearRatio { get; set; } = 1;
            public float YGearRatio { get; set; } = 1;
            public float ZGearRatio { get; set; } = 1;
            public LastDirection XLastDirection => LastDirection.Positive;
            public LastDirection YLastDirection => LastDirection.Positive;
            public LastDirection ZLastDirection => LastDirection.Positive;

            /// <summary>Where the controller says the axes are the next time it is asked.</summary>
            public float ReportedX;
            public float ReportedY;

            /// <summary>When set, absolute moves stay in progress until this task completes.</summary>
            public Task HoldAbsoluteMoves;

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) => Task.CompletedTask;

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
                AbsoluteMoves.Add((axis, position));
                return HoldAbsoluteMoves ?? Task.CompletedTask;
            }

            public Task RefreshStatus(CancellationToken token) {
                XPosition1 = ReportedX;
                YPosition1 = ReportedY;
                return Task.CompletedTask;
            }

            public void Dispose() { }
        }

        private static (UniversalPolarAlignmentOAPAVM vm, FakeSystem system) Vm(float xRatio, float yRatio) {
            var system = new FakeSystem();
            var vm = new OapaTestVm { Hardware = system };
            vm.XGearRatio = xRatio;
            vm.YGearRatio = yRatio;
            // Arranged values are test fixtures, not manual user edits: keep Apply single-step.
            Properties.Settings.Default.OAPAXGearRatioSource = "Default";
            Properties.Settings.Default.OAPAYGearRatioSource = "Default";
            Properties.Settings.Default.OAPAXBacklashSource = "Default";
            Properties.Settings.Default.OAPAYBacklashSource = "Default";
            return (vm, system);
        }

        [Test]
        public async Task GoHome_AfterManualXRatioEdit_ReturnsToSameControllerPosition() {
            var (vm, system) = Vm(xRatio: 100, yRatio: 100);
            system.ReportedX = 2f;   // controller position 200
            system.ReportedY = 0f;
            await vm.SetHome(CancellationToken.None);

            vm.XGearRatio = 200;
            await vm.GoHome(CancellationToken.None);

            // 1 x 200 drives the controller back to 200; the stale logical value would command 2 x 200 = 400.
            system.AbsoluteMoves.Should().Equal((Axis.XAxis, 1f), (Axis.YAxis, 0f));
        }

        [Test]
        public async Task GoHome_AfterManualYRatioEdit_ReturnsToSameControllerPosition() {
            var (vm, system) = Vm(xRatio: 100, yRatio: 100);
            system.ReportedX = 0f;
            system.ReportedY = 3f;   // controller position 300
            await vm.SetHome(CancellationToken.None);

            vm.YGearRatio = 300;
            await vm.GoHome(CancellationToken.None);

            system.AbsoluteMoves.Should().Equal((Axis.XAxis, 0f), (Axis.YAxis, 1f));
        }

        [Test]
        public async Task GoHome_AfterApplyCalibration_ReturnsToSameControllerPositionOnBothAxes() {
            var (vm, system) = Vm(xRatio: 100, yRatio: 100);
            system.ReportedX = 2f;   // controller position 200
            system.ReportedY = 4f;   // controller position 400
            await vm.SetHome(CancellationToken.None);

            vm.DiscoveredXRatio = 200;
            vm.DiscoveredYRatio = 400;
            vm.DiscoveredXBacklash = 0f;
            vm.DiscoveredYBacklash = 0f;
            vm.DiscoveredReverseAzimuth = vm.ReverseAzimuth;
            vm.DiscoveredReverseAltitude = vm.ReverseAltitude;
            vm.HasCalibrationResult = true;
            vm.ApplyCalibration();

            await vm.GoHome(CancellationToken.None);

            system.AbsoluteMoves.Should().Equal((Axis.XAxis, 1f), (Axis.YAxis, 1f));
        }

        [Test]
        public async Task HomeDisplay_TracksRatioChanges_WhileHomeIsSet() {
            var (vm, system) = Vm(xRatio: 100, yRatio: 100);
            system.ReportedX = 2f;
            system.ReportedY = 4f;
            await vm.SetHome(CancellationToken.None);
            vm.HomeX.Should().Be(2f);
            vm.HomeY.Should().Be(4f);

            // The panel shows Home next to the logical position, so the displayed value must
            // be re-expressed under the new factor while it keeps marking the same physical spot.
            vm.XGearRatio = 200;
            vm.YGearRatio = 400;
            vm.HomeX.Should().Be(1f);
            vm.HomeY.Should().Be(1f);
        }

        [Test]
        public async Task SetHome_StoresWhereTheControllerIsNow_NotTheLastPolledPosition() {
            // The panel's position is refreshed by a background poll, so a Set Home pressed right
            // after a move can read where the axis was a poll ago. Home is where the controller
            // says the axis is at the moment of the press.
            var (vm, system) = Vm(xRatio: 100, yRatio: 100);
            vm.PositionX = 0f;   // last poll
            vm.PositionY = 0f;
            system.ReportedX = 2f;
            system.ReportedY = 4f;

            await vm.SetHome(CancellationToken.None);

            vm.HomeX.Should().Be(2f);
            vm.HomeY.Should().Be(4f);
        }

        [Test]
        public async Task WhileGoHomeIsMoving_TheFactorsCannotChange_AndApplyIsUnavailable() {
            // Go Home computes both targets up front and drives the axes one after the other. A
            // factor changed between the two moves - typed in, or written by Apply - makes the
            // second move execute a target calculated under the old factor with the new one.
            var (vm, system) = Vm(xRatio: 100, yRatio: 100);
            system.ReportedX = 2f;
            system.ReportedY = 4f;
            await vm.SetHome(CancellationToken.None);
            vm.DiscoveredXRatio = 200;
            vm.DiscoveredYRatio = 400;
            vm.DiscoveredReverseAzimuth = vm.ReverseAzimuth;
            vm.DiscoveredReverseAltitude = vm.ReverseAltitude;
            vm.HasCalibrationResult = true;
            var release = new TaskCompletionSource();
            system.HoldAbsoluteMoves = release.Task;

            var goingHome = vm.GoHome(CancellationToken.None);

            vm.IsNotMoving.Should().BeFalse("the first axis is still moving");
            vm.CanApplyCalibration().Should().BeFalse();
            vm.ApplyCalibration();
            vm.XGearRatio = 300;
            vm.XGearRatio.Should().Be(100f, "neither Apply nor an edit may change a factor mid-move");
            vm.YGearRatio.Should().Be(100f);

            release.SetResult();
            await goingHome;

            vm.CanApplyCalibration().Should().BeTrue("the result is still there once the axes have stopped");
        }
    }
}
