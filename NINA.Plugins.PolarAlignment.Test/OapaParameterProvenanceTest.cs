using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Every calibration parameter carries its provenance (default / manual / calibrated),
    /// hand-entered values are validated at the door, and applying a calibration never
    /// silently replaces a manual value: the first Apply arms an explicit confirmation.
    /// </summary>
    public class OapaParameterProvenanceTest {

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
            // The settings are static, and a value is only marked Manual when it actually
            // changes - so a test inheriting the previous test's numbers would silently
            // stop arming the confirmation. Reset the values as well as their provenance.
            Properties.Settings.Default.OAPAXGearRatio = 1f;
            Properties.Settings.Default.OAPAYGearRatio = 1f;
            Properties.Settings.Default.OAPAXBacklashCompensation = 0f;
            Properties.Settings.Default.OAPAYBacklashCompensation = 0f;
            Properties.Settings.Default.OAPAXGearRatioSource = "Default";
            Properties.Settings.Default.OAPAYGearRatioSource = "Default";
            Properties.Settings.Default.OAPAXBacklashSource = "Default";
            Properties.Settings.Default.OAPAYBacklashSource = "Default";
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
            vm.upa = new FakeSystem();
            return vm;
        }

        [Test]
        public void FreshParameters_ReportDefaultProvenance() {
            var vm = Vm();

            vm.XGearRatioSource.Should().Be(OapaParameterSource.Default);
            vm.YBacklashSource.Should().Be(OapaParameterSource.Default);
        }

        [Test]
        public void UserEdit_MarksTheParameterManual_ButOnlyOnARealChange() {
            var vm = Vm();

            vm.YBacklashCompensation = vm.YBacklashCompensation;
            vm.YBacklashSource.Should().Be(OapaParameterSource.Default, "re-writing the same value is not a manual edit");

            vm.YBacklashCompensation = vm.YBacklashCompensation + 2f;
            vm.YBacklashSource.Should().Be(OapaParameterSource.Manual);

            vm.XGearRatio = vm.XGearRatio + 10f;
            vm.XGearRatioSource.Should().Be(OapaParameterSource.Manual);
        }

        [Test]
        public void BacklashSourceLabels_ExistForBothAxes_AndReflectManualEdits() {
            var vm = Vm();

            // Empty for factory defaults, like every other provenance hint.
            vm.XBacklashSourceLabel.Should().BeEmpty();
            vm.YBacklashSourceLabel.Should().BeEmpty();

            vm.XBacklashCompensation = vm.XBacklashCompensation + 2f;
            vm.XBacklashSourceLabel.Should().Be("manual");
        }

        [Test]
        public void HandEnteredBacklash_IsClampedToAPhysicalRange() {
            var vm = Vm();

            // A real field entry: a step count typed into an arcmin field.
            vm.YBacklashCompensation = 20600f;
            vm.YBacklashCompensation.Should().Be(90f);
            vm.YBacklashSource.Should().Be(OapaParameterSource.Manual);

            vm.YBacklashCompensation = -3f;
            vm.YBacklashCompensation.Should().Be(0f);
        }

        [Test]
        public void HandEnteredFactor_IsClampedToItsRange() {
            var vm = Vm();

            vm.XGearRatio = 0.5f;
            vm.XGearRatio.Should().Be(1f);

            vm.XGearRatio = 1e9f;
            vm.XGearRatio.Should().Be(100000f);
        }

        [Test]
        public void Apply_WithAManualValue_ArmsConfirmationInsteadOfOverwriting() {
            var vm = Vm();
            vm.YBacklashCompensation = 5f;   // manual
            var factorBefore = vm.XGearRatio;
            PrepareResult(vm);

            vm.ApplyCalibration();

            vm.ApplyConfirmationPending.Should().BeTrue();
            vm.YBacklashCompensation.Should().Be(5f, "nothing may be overwritten before the confirmation");
            vm.XGearRatio.Should().Be(factorBefore);
            vm.CalibrationStatus.Should().Contain("manually");
            vm.CalibrationStatus.Should().Contain("Apply again");
            vm.CalibrationStatus.Should().Contain("Y backlash");
        }

        [Test]
        public void SecondApply_Confirms_AppliesAndMarksEverythingCalibrated() {
            var vm = Vm();
            vm.YBacklashCompensation = 5f;   // manual
            PrepareResult(vm);

            vm.ApplyCalibration();
            vm.ApplyCalibration();

            vm.ApplyConfirmationPending.Should().BeFalse();
            vm.XGearRatio.Should().Be(400f);
            vm.YBacklashCompensation.Should().Be(2f);
            vm.XGearRatioSource.Should().Be(OapaParameterSource.Calibrated);
            vm.YGearRatioSource.Should().Be(OapaParameterSource.Calibrated);
            vm.XBacklashSource.Should().Be(OapaParameterSource.Calibrated);
            vm.YBacklashSource.Should().Be(OapaParameterSource.Calibrated);
        }

        [Test]
        public void Apply_WithoutManualValues_IsSingleStep() {
            var vm = Vm();
            PrepareResult(vm);

            vm.ApplyCalibration();

            vm.ApplyConfirmationPending.Should().BeFalse();
            vm.XGearRatio.Should().Be(400f);
            vm.XGearRatioSource.Should().Be(OapaParameterSource.Calibrated);
            vm.CalibrationStatus.Should().Contain("Applied");
        }

        [Test]
        public void ApplyButton_SaysWhatItWants_WhileTheConfirmationIsArmed() {
            // A tester lost a good calibration to this: the request for a second press
            // lived only in the status line, so "nothing happened" was the natural reading.
            var vm = Vm();
            vm.ApplyButtonText.Should().Be("Apply");

            vm.YBacklashCompensation = 5f;   // manual
            PrepareResult(vm);
            vm.ApplyCalibration();

            vm.ApplyButtonText.Should().Be("Apply again to confirm");

            vm.DiscardCalibration();
            vm.ApplyButtonText.Should().Be("Apply");
        }

        [Test]
        public void Discard_DisarmsThePendingConfirmation() {
            var vm = Vm();
            vm.YBacklashCompensation = 5f;
            PrepareResult(vm);

            vm.ApplyCalibration();
            vm.ApplyConfirmationPending.Should().BeTrue();

            vm.DiscardCalibration();

            vm.ApplyConfirmationPending.Should().BeFalse();
            vm.YBacklashCompensation.Should().Be(5f);
        }

        [Test]
        public void Provenance_PersistsAcrossVmInstances() {
            var vm = Vm();
            vm.XGearRatio = vm.XGearRatio + 7f;

            var second = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
            second.XGearRatioSource.Should().Be(OapaParameterSource.Manual);
        }

        [Test]
        public void ANegativeDirectionNeverSet_FollowsThePositiveValue() {
            // -1 is "never set": an axis configured before the per-direction pair existed has
            // to stay symmetric, not silently acquire a zero compensation one way.
            var vm = Vm();
            Properties.Settings.Default.OAPAXBacklashCompensationNegative = -1f;
            Properties.Settings.Default.OAPAYBacklashCompensationNegative = -1f;

            vm.XBacklashCompensation = 7f;
            vm.YBacklashCompensation = 3f;

            vm.XBacklashCompensationNegative.Should().Be(7f);
            vm.YBacklashCompensationNegative.Should().Be(3f);
        }

        [Test]
        public void AHandEnteredNegativeDirection_IsClampedAndMarkedManual() {
            var vm = Vm();
            Properties.Settings.Default.OAPAXBacklashCompensationNegative = -1f;
            Properties.Settings.Default.OAPAYBacklashCompensationNegative = -1f;

            vm.XBacklashCompensationNegative = 20600f;
            vm.XBacklashCompensationNegative.Should().Be(90f);
            vm.XBacklashSource.Should().Be(OapaParameterSource.Manual);

            vm.YBacklashCompensationNegative = -3f;
            vm.YBacklashCompensationNegative.Should().Be(0f, "a negative play is clamped to none, not stored as the 'never set' sentinel");
            vm.YBacklashSource.Should().Be(OapaParameterSource.Manual);
        }

        private static void PrepareResult(UniversalPolarAlignmentOAPAVM vm) {
            vm.DiscoveredXRatio = 400f;
            vm.DiscoveredYRatio = 200f;
            vm.DiscoveredXBacklash = 1f;
            vm.DiscoveredYBacklash = 2f;
            // Both directions, as a real pass always produces: leaving the negative side at
            // zero would read as a direction split, which Apply now collapses to the mean
            // until a second calibration confirms it. These tests are about provenance.
            vm.DiscoveredXBacklashNegative = 1f;
            vm.DiscoveredYBacklashNegative = 2f;
            vm.HasCalibrationResult = true;
            vm.CalibrationDirectionalBacklash = false;
        }
    }
}
