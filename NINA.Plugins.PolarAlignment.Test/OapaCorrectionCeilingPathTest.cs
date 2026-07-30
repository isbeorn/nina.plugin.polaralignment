using FluentAssertions;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Plugins.PolarAlignment.Avalon;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Drives the OAPA correction-ceiling feature through its production path: the policy
    /// calculation on the OAPA VM (persisted-settings backed), the selected-system dispatch
    /// on the plugin, and the TPAPAVM.MoveCloser handoff that configures the controller
    /// each cycle.
    /// </summary>
    public class OapaCorrectionCeilingPathTest {

        [Test]
        public void OapaPolicy_ScalesWithErrorBetweenFloorAndPersistedCeiling() {
            var vm = new UniversalPolarAlignmentOAPAVM(null);
            vm.MaxCorrectionMagnitude = 20;

            // Small error: the controller default is the floor for a gentle final approach.
            vm.GetMaximumCorrectionMagnitude(3).Should().Be(AutomatedAdjustmentController.DefaultMaximumMoveMagnitude);
            // Mid error: 80% of the measured error.
            vm.GetMaximumCorrectionMagnitude(10).Should().BeApproximately(8, 1e-9);
            // Large error: the persisted user ceiling wins.
            vm.GetMaximumCorrectionMagnitude(60).Should().Be(20);
        }

        [Test]
        public void MaxCorrectionMagnitude_OwnerClampsAndPersistsTheClampedValue() {
            // The OAPA VM is the sole owner of the persisted setting: the 1-60 invariant
            // must hold on disk no matter which public surface wrote the value.
            var vm = new UniversalPolarAlignmentOAPAVM(null);

            vm.MaxCorrectionMagnitude = 0.2;
            vm.MaxCorrectionMagnitude.Should().Be(AutomatedAdjustmentController.MinimumConfigurableMoveMagnitude);
            Properties.Settings.Default.OAPAMaxCorrectionMagnitude.Should().Be(AutomatedAdjustmentController.MinimumConfigurableMoveMagnitude);

            vm.MaxCorrectionMagnitude = 500;
            vm.MaxCorrectionMagnitude.Should().Be(AutomatedAdjustmentController.MaximumConfigurableMoveMagnitude);
            Properties.Settings.Default.OAPAMaxCorrectionMagnitude.Should().Be(AutomatedAdjustmentController.MaximumConfigurableMoveMagnitude);

            vm.MaxCorrectionMagnitude = 12;
            vm.MaxCorrectionMagnitude.Should().Be(12);
        }

        [Test]
        public void EffectiveCeiling_RespectsTheConfigurableRange_AfterAnOutOfRangeWrite() {
            var vm = new UniversalPolarAlignmentOAPAVM(null);
            vm.MaxCorrectionMagnitude = 500;

            vm.GetMaximumCorrectionMagnitude(600).Should().Be(AutomatedAdjustmentController.MaximumConfigurableMoveMagnitude);
        }

        [Test]
        public void SelectedSystemDispatch_ReturnsTheMatchingVM() {
            var upas = new UniversalPolarAlignmentVM(null);
            var oapa = new UniversalPolarAlignmentOAPAVM(null);
            PolarAlignmentPlugin.UniversalPolarAlignmentVM = upas;
            PolarAlignmentPlugin.UniversalPolarAlignmentOAPAVM = oapa;

            Properties.Settings.Default.SelectedPolarAlignmentSystem = "OAPA";
            PolarAlignmentPlugin.ActiveAlignmentSystemVM.Should().BeSameAs(oapa);

            Properties.Settings.Default.SelectedPolarAlignmentSystem = "UPAS";
            PolarAlignmentPlugin.ActiveAlignmentSystemVM.Should().BeSameAs(upas);
        }

        [Test]
        public async Task MoveCloser_ConfiguresControllerFromTheActiveOapaSystem() {
            var vm = PrepareTpapavm(selectedSystem: "OAPA", totalErrorDegrees: 1.0);
            PolarAlignmentPlugin.UniversalPolarAlignmentOAPAVM.MaxCorrectionMagnitude = 20;

            await vm.MoveCloser(null, CancellationToken.None);

            // 60' of error: auto-scaled 48 is capped by the persisted user ceiling of 20.
            vm.automatedAdjustmentController.MaximumMoveMagnitude.Should().Be(20);
            vm.automatedAdjustmentController.AggressiveCorrections.Should().BeTrue();
        }

        [Test]
        public async Task MoveCloser_KeepsLegacyControllerConfigurationForUpas() {
            var vm = PrepareTpapavm(selectedSystem: "UPAS", totalErrorDegrees: 1.0);

            await vm.MoveCloser(null, CancellationToken.None);

            vm.automatedAdjustmentController.MaximumMoveMagnitude.Should().Be(AutomatedAdjustmentController.DefaultMaximumMoveMagnitude);
            vm.automatedAdjustmentController.AggressiveCorrections.Should().BeFalse();
        }

        /// <summary>
        /// Builds a TPAPAVM whose MoveCloser reaches the controller-configuration handoff
        /// and then exits on the no-observation skip plan, so no nudge is ever commanded.
        /// </summary>
        private static TPAPAVM PrepareTpapavm(string selectedSystem, double totalErrorDegrees) {
            PolarAlignmentPlugin.UniversalPolarAlignmentVM = new UniversalPolarAlignmentVM(null);
            PolarAlignmentPlugin.UniversalPolarAlignmentOAPAVM = new UniversalPolarAlignmentOAPAVM(null);
            Properties.Settings.Default.SelectedPolarAlignmentSystem = selectedSystem;
            Properties.Settings.Default.DoAutomatedAdjustments = true;
            Properties.Settings.Default.UseContinuousErrorEstimator = false;

            var vm = new TPAPAVM(null, null) {
                PolarErrorDetermination = BuildErrorDetermination(totalErrorDegrees)
            };
            return vm;
        }

        internal static PolarErrorDetermination BuildErrorDetermination(double totalErrorDegrees) {
            var latitude = Angle.ByDegree(49);
            var longitude = Angle.ByDegree(7);
            var elevation = 250d;
            var time = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var refraction = new RefractionParameters(0, 0.0001, 0, 0);

            var solve1 = new Coordinates(Angle.ByDegree(20), Angle.ByDegree(40), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position1 = new Position(solve1, 0, latitude, longitude, elevation, refraction);
            var solve2 = new Coordinates(Angle.ByDegree(60), Angle.ByDegree(41), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position2 = new Position(solve2, 0, latitude, longitude, elevation, refraction);
            var solve3 = new Coordinates(Angle.ByDegree(90), Angle.ByDegree(42), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position3 = new Position(solve3, 0, latitude, longitude, elevation, refraction);

            var determination = new PolarErrorDetermination(
                new PlateSolving.PlateSolveResult() { Coordinates = solve3 },
                position1, position2, position3, latitude, longitude, elevation, refraction, false);
            determination.CurrentMountAxisTotalError = Angle.ByDegree(totalErrorDegrees);
            return determination;
        }

        internal sealed class CustomTime : ICustomDateTime {
            private readonly DateTime time;
            public CustomTime(DateTime time) { this.time = time; }
            public DateTime Now => time;
            public DateTime UtcNow => time;
        }
    }
}
