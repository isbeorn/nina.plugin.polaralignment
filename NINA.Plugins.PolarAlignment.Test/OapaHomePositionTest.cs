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
            public float XPosition1 => 0;
            public float YPosition1 => 0;
            public float ZPosition1 => 0;
            public float XGearRatio { get; set; } = 1;
            public float YGearRatio { get; set; } = 1;
            public float ZGearRatio { get; set; } = 1;
            public LastDirection XLastDirection => LastDirection.Positive;
            public LastDirection YLastDirection => LastDirection.Positive;
            public LastDirection ZLastDirection => LastDirection.Positive;

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) => Task.CompletedTask;

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
                AbsoluteMoves.Add((axis, position));
                return Task.CompletedTask;
            }

            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }
        }

        private static (UniversalPolarAlignmentOAPAVM vm, FakeSystem system) Vm(float xRatio, float yRatio) {
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
            var system = new FakeSystem();
            vm.upa = system;
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
            vm.PositionX = 2f;   // controller position 200
            vm.PositionY = 0f;
            vm.SetHome();

            vm.XGearRatio = 200;
            await vm.GoHome(CancellationToken.None);

            // 1 x 200 drives the controller back to 200; the stale logical value would command 2 x 200 = 400.
            system.AbsoluteMoves.Should().Equal((Axis.XAxis, 1f), (Axis.YAxis, 0f));
        }

        [Test]
        public async Task GoHome_AfterManualYRatioEdit_ReturnsToSameControllerPosition() {
            var (vm, system) = Vm(xRatio: 100, yRatio: 100);
            vm.PositionX = 0f;
            vm.PositionY = 3f;   // controller position 300
            vm.SetHome();

            vm.YGearRatio = 300;
            await vm.GoHome(CancellationToken.None);

            system.AbsoluteMoves.Should().Equal((Axis.XAxis, 0f), (Axis.YAxis, 1f));
        }

        [Test]
        public async Task GoHome_AfterApplyCalibration_ReturnsToSameControllerPositionOnBothAxes() {
            var (vm, system) = Vm(xRatio: 100, yRatio: 100);
            vm.PositionX = 2f;   // controller position 200
            vm.PositionY = 4f;   // controller position 400
            vm.SetHome();

            vm.DiscoveredXRatio = 200;
            vm.DiscoveredYRatio = 400;
            vm.DiscoveredXBacklash = 0f;
            vm.DiscoveredYBacklash = 0f;
            vm.HasCalibrationResult = true;
            vm.ApplyCalibration();

            await vm.GoHome(CancellationToken.None);

            system.AbsoluteMoves.Should().Equal((Axis.XAxis, 1f), (Axis.YAxis, 1f));
        }

        [Test]
        public void HomeDisplay_TracksRatioChanges_WhileHomeIsSet() {
            var (vm, _) = Vm(xRatio: 100, yRatio: 100);
            vm.PositionX = 2f;
            vm.PositionY = 4f;
            vm.SetHome();
            vm.HomeX.Should().Be(2f);
            vm.HomeY.Should().Be(4f);

            // The panel shows Home next to the logical position, so the displayed value must
            // be re-expressed under the new factor while it keeps marking the same physical spot.
            vm.XGearRatio = 200;
            vm.YGearRatio = 400;
            vm.HomeX.Should().Be(1f);
            vm.HomeY.Should().Be(1f);
        }
    }
}
