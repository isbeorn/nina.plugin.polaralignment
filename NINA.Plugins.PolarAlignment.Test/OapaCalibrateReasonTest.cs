using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Why the Calibrate button is disabled.
    ///
    /// Field report (18/08): an alignment halted, the halt message told the user to
    /// "re-run the OAPA Self-Calibration", and the button was greyed out with no
    /// explanation. A halt pauses the alignment rather than ending it, and a paused
    /// alignment still owns the camera - so the one remedy we had just recommended was
    /// silently unavailable. The user rebooted the machine. The button state was right;
    /// the silence was the defect.
    /// </summary>
    public class OapaCalibrateReasonTest {

        private sealed class FakeSystem : IPolarAlignmentSystem {
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
            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) => Task.CompletedTask;
            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }
        }

        private static UniversalPolarAlignmentOAPAVM Vm() {
            return new OapaTestVm { Hardware = new FakeSystem() };
        }

        [Test]
        public void WhenNothingIsInTheWay_ThereIsNoReason() {
            UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: true, moving: false, calibrating: false, cameraBusy: false).Should().BeEmpty();
        }

        [Test]
        public void NotConnected_IsSaidFirst() {
            // Reported ahead of everything else even when other conditions also fail: it is
            // the only one the user can act on without knowing anything about the rest.
            var reason = UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: false, moving: true, calibrating: false, cameraBusy: true);

            reason.Should().ContainEquivalentOf("connect");
        }

        [Test]
        public void AMovingAxis_IsSaidSo() {
            UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: true, moving: true, calibrating: false, cameraBusy: false)
                .Should().Contain("moving");
        }

        [Test]
        public void ACalibrationAlreadyRunning_IsSaidSo() {
            UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: true, moving: false, calibrating: true, cameraBusy: false)
                .Should().Contain("already");
        }

        [Test]
        public void ACameraHeldByAnAlignment_CarriesTheRemedy_NotJustTheDiagnosis() {
            // The 18/08 case. Saying "the camera is busy" would have left the user exactly
            // where they were: what unblocks them is knowing that the halted alignment is
            // still running and has to be stopped.
            var reason = UniversalPolarAlignmentOAPAVM.CalibrationBlockedBy(
                connected: true, moving: false, calibrating: false, cameraBusy: true);

            reason.Should().Contain("camera").And.Contain("alignment").And.Contain("Stop");
        }

        [Test]
        public void TheReasonIsEmptyExactlyWhenTheButtonIsEnabled() {
            // The invariant that keeps the two from drifting apart: a disabled button always
            // has something to say, an enabled one never does.
            foreach (var connected in new[] { false, true }) {
                foreach (var moving in new[] { false, true }) {
                    var vm = Vm();
                    vm.Connected = connected;
                    vm.IsNotMoving = !moving;

                    vm.CalibrateUnavailableReason.Any().Should().Be(!vm.CanCalibrate(),
                        $"connected={connected}, moving={moving}");
                }
            }
        }
    }
}
