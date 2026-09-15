using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Wire grammar for the TMC driver configuration commands. The firmware dispatcher
    /// only recognises the type-first form (C/H/S followed by the axis letter, e.g.
    /// "CX600", "HY50"); an axis-first string like "XH50" falls through to the
    /// unknown-command branch and is silently ignored. These tests pin the grammar the
    /// plugin puts on the wire to the one the firmware actually parses.
    /// </summary>
    public class OapaDriverCommandsTest {

        [Test]
        public void RunCurrent_IsTypeFirst_ForXAxis() {
            OapaDriverCommands.RunCurrent(Axis.XAxis, 600).Should().Be("CX600");
        }

        [Test]
        public void RunCurrent_IsTypeFirst_ForYAxis() {
            OapaDriverCommands.RunCurrent(Axis.YAxis, 800).Should().Be("CY800");
        }

        [Test]
        public void HoldPercent_IsTypeFirst_ForXAxis() {
            OapaDriverCommands.HoldPercent(Axis.XAxis, 50).Should().Be("HX50");
        }

        [Test]
        public void HoldPercent_IsTypeFirst_ForYAxis() {
            OapaDriverCommands.HoldPercent(Axis.YAxis, 35).Should().Be("HY35");
        }

        [Test]
        public void Microsteps_IsTypeFirst() {
            OapaDriverCommands.Microsteps(Axis.XAxis, 16).Should().Be("SX16");
            OapaDriverCommands.Microsteps(Axis.YAxis, 4).Should().Be("SY4");
        }

        [Test]
        public void StartupBatch_SendsEveryStoredValue_WithMicrostepsFirst() {
            var batch = OapaDriverCommands.StartupBatch(
                xRunMA: 600, xHoldPercent: 50, yRunMA: 700, yHoldPercent: 40,
                xMicrosteps: 16, yMicrosteps: 4);

            // Microstepping changes what a step means, so it must be set before anything can move.
            batch.Should().Equal("SX16", "SY4", "CX600", "HX50", "CY700", "HY40");
        }

        [Test]
        public void StartupBatch_CarriesMicrosteps_BecauseTheControllerForgetsThemAtPowerOff() {
            var batch = OapaDriverCommands.StartupBatch(600, 50, 700, 40, 4, 8);

            batch.Should().Contain("SX4").And.Contain("SY8",
                because: "a microstep setting lost at power-off leaves every commanded move wrong by that ratio");
        }
    }
}
