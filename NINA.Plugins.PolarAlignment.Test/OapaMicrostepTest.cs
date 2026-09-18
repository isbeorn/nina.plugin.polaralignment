using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Steps per arcminute scale exactly with the microstep setting, so changing it
    /// invalidates the calibration factor by a known ratio. Rescaling the factor with it is
    /// not a convenience: a stale factor makes every commanded move wrong by that same ratio,
    /// and on a short-travel platform the first move drives an axis into its end stop.
    /// </summary>
    public class OapaMicrostepTest {

        private static UniversalPolarAlignmentOAPAVM Vm(int microsteps, float xRatio, float yRatio) {
            Properties.Settings.Default.OAPAXMicrosteps = microsteps;
            Properties.Settings.Default.OAPAYMicrosteps = microsteps;
            Properties.Settings.Default.OAPAXGearRatio = xRatio;
            Properties.Settings.Default.OAPAYGearRatio = yRatio;
            Properties.Settings.Default.OAPAXGearRatioSource = "Calibrated";
            Properties.Settings.Default.OAPAYGearRatioSource = "Calibrated";
            return new OapaTestVm();
        }

        [Test]
        public void HalvingTheMicrosteps_HalvesTheCalibrationFactor() {
            var vm = Vm(microsteps: 16, xRatio: 400f, yRatio: 800f);

            vm.XMicrosteps = 8;

            vm.XMicrosteps.Should().Be(8);
            vm.XGearRatio.Should().BeApproximately(200f, 0.01f, "half the microsteps means half the steps per arcminute");
            vm.YGearRatio.Should().BeApproximately(800f, 0.01f, "the other axis is untouched");
        }

        [Test]
        public void DoublingTheMicrosteps_DoublesIt_OnTheAxisItWasAskedFor() {
            var vm = Vm(microsteps: 16, xRatio: 400f, yRatio: 800f);

            vm.YMicrosteps = 32;

            vm.YGearRatio.Should().BeApproximately(1600f, 0.01f);
            vm.XGearRatio.Should().BeApproximately(400f, 0.01f);
        }

        [Test]
        public void TheFactorKeepsItsProvenance_AcrossTheRescale() {
            // The rescale is arithmetic on a measured value, not a new measurement and not a
            // hand edit: a calibrated factor stays calibrated, or the next Apply would start
            // asking for confirmation over a value the user never typed.
            var vm = Vm(microsteps: 16, xRatio: 400f, yRatio: 400f);

            vm.XMicrosteps = 32;

            vm.XGearRatioSource.Should().Be(OapaParameterSource.Calibrated);
        }

        [Test]
        public void AValueTheDriverCannotDo_IsRefused() {
            var vm = Vm(microsteps: 16, xRatio: 400f, yRatio: 400f);

            vm.XMicrosteps = 3;

            vm.XMicrosteps.Should().Be(16, "only the driver's own microstep values are accepted");
            vm.XGearRatio.Should().BeApproximately(400f, 0.01f, "and nothing is rescaled");
        }

        [Test]
        public void SettingTheSameValueAgain_ChangesNothing() {
            var vm = Vm(microsteps: 16, xRatio: 400f, yRatio: 400f);

            vm.XMicrosteps = 16;

            vm.XGearRatio.Should().BeApproximately(400f, 0.01f, "a no-op write must not rescale the factor");
        }

        [Test]
        public void TheOfferedValues_AreTheDriversOwn_AndAscending() {
            var vm = Vm(microsteps: 16, xRatio: 400f, yRatio: 400f);

            vm.MicrostepOptions.Should().Equal(1, 2, 4, 8, 16, 32, 64, 128, 256);
        }

        /// <summary>Scripted controller, to see what the change puts on the wire.</summary>
        private sealed class ScriptedLink : ISerialLink {
            public readonly System.Collections.Generic.List<string> Writes = new();
            private readonly System.Collections.Generic.Queue<string> pending = new();

            public bool IsOpen => true;

            public void WriteLine(string text) {
                Writes.Add(text);
                if (text == "?") {
                    pending.Enqueue("<Idle|MPos:0.00,0.00,0.00|V:1.2.2|>");
                    pending.Enqueue("ok");
                } else {
                    pending.Enqueue("ok");
                }
            }

            public string ReadLine() {
                if (pending.Count == 0) { throw new System.TimeoutException("no reply"); }
                return pending.Dequeue();
            }

            public void Dispose() { }
        }

        private sealed class TestableOapa : UniversalPolarAlignmentOAPA {
            public TestableOapa(ISerialLink link) : base(link) { }
        }

        [Test]
        public void OnAConnectedController_TheNewValueReachesTheDriver() {
            // The setting is volatile on the controller, so the panel has to push it. Without
            // this the field would show a value the driver never received, and every move
            // would be wrong by the ratio between the two.
            var vm = Vm(microsteps: 16, xRatio: 400f, yRatio: 400f);
            var link = new ScriptedLink();
            ((OapaTestVm)vm).Hardware = new TestableOapa(link);
            vm.Connected = true;
            link.Writes.Clear();

            vm.XMicrosteps = 8;

            link.Writes.Should().Contain("SX8", "the microstep command is type-first, like every other driver command");
        }

        [Test]
        public void ThePhysicalSpeedFollowsTheRescale() {
            // The reading is steps per second divided by steps per arcminute: both halves
            // change here, and the panel has to show the new one.
            var vm = Vm(microsteps: 16, xRatio: 400f, yRatio: 400f);
            vm.XSpeed = 1000;
            vm.XSpeedPhysical.Should().Be("~ 2.5 '/s");

            vm.XMicrosteps = 8;

            vm.XSpeedPhysical.Should().Be("~ 5.0 '/s");
        }
    }
}
