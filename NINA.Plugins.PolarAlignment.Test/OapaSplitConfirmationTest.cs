using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// A per-direction backlash split is only applied once a second calibration agrees with
    /// the first about which direction costs more. Until then, and whenever the heavier side
    /// flips between calibrations, Apply collapses the pair to its mean.
    /// </summary>
    public class OapaSplitConfirmationTest {

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
        public void WhatApplyReports_IsWhatTheAxisWillUse_NotWhatWasMeasured() {
            // Field log (18/08) read, in the same second:
            //   "applying the mean 2.99' to both directions"
            //   "OAPA calibration applied: ... backlash X=+1.66'/-4.32' ..."
            // Whoever reads the second line believes the split was applied. The panel's status
            // line carried the same contradiction, and it is the only place a user can look.
            var vm = ApplyYPair(2.19f, 0.68f);

            vm.CalibrationStatus.Should().Contain("1.44", "the mean is what the axis will use");
            vm.CalibrationStatus.Should().NotContain("2.19");
            vm.CalibrationStatus.Should().NotContain("0.68");
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
    }
}
