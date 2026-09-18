using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The speed setting is a step rate, and the same number means very different sky
    /// speeds on the two axes (a tester's rig: 1000 steps/s is ~74 '/s in azimuth but
    /// ~8.6 '/s in altitude). Rather than hide the unit behind a percentage, the panel
    /// shows what the rate actually is once a calibration factor makes it computable.
    /// </summary>
    public class OapaSpeedDisplayTest {

        private static UniversalPolarAlignmentOAPAVM Vm() {
            Properties.Settings.Default.OAPAXGearRatio = 1f;
            Properties.Settings.Default.OAPAYGearRatio = 1f;
            Properties.Settings.Default.OAPAXGearRatioSource = "Default";
            Properties.Settings.Default.OAPAYGearRatioSource = "Default";
            return new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
        }

        [Test]
        public void PhysicalSpeed_IsDerivedFromTheCalibrationFactor() {
            var vm = Vm();

            vm.XSpeed = 1000;
            vm.XGearRatio = 100f;

            vm.XSpeedPhysical.Should().Be("~ 10.0 '/s");
        }

        [Test]
        public void PhysicalSpeed_IsShownPerAxis_SoTheTwoCanBeCompared() {
            var vm = Vm();

            vm.XSpeed = 1000;
            vm.XGearRatio = 13.43f;   // azimuth
            vm.YSpeed = 1000;
            vm.YGearRatio = 116.29f;  // altitude

            vm.XSpeedPhysical.Should().Be("~ 74.5 '/s");
            vm.YSpeedPhysical.Should().Be("~ 8.6 '/s");
        }

        [Test]
        public void PhysicalSpeed_StaysEmpty_UntilAFactorIsKnown() {
            var vm = Vm();

            vm.XSpeed = 1000;

            vm.XSpeedPhysical.Should().BeEmpty("a factor of 1 means the platform has never been calibrated");
        }

        [Test]
        public void PhysicalSpeed_FollowsBothItsInputs() {
            var vm = Vm();
            vm.XGearRatio = 100f;

            var seen = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.XSpeedPhysical)) { seen.Add(e.PropertyName); } };

            vm.XSpeed = 500;
            vm.XGearRatio = 50f;

            seen.Should().HaveCount(2, "the reading must refresh when either the speed or the factor changes");
            vm.XSpeedPhysical.Should().Be("~ 10.0 '/s");
        }
    }
}
