using System;
using System.Globalization;
using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Contract between the plugin and the firmware's command dispatcher. The firmware
    /// acknowledges every line with "ok" — including commands it does not understand —
    /// so a grammar mismatch is invisible on the wire: the plugin gets its "ok" and the
    /// hardware silently does nothing. That is exactly how the axis-first driver commands
    /// ("XH50") went unnoticed while the firmware only parses type-first ("HX50").
    ///
    /// FirmwareDispatcher below is a line-by-line C# port of dispatchCommand() and its
    /// helpers in firmware/oapa.ino. If the firmware grammar changes, update the port
    /// here in the same commit — this file is the executable protocol spec.
    /// </summary>
    public class OapaFirmwareWireContractTest {

        private enum Handler { Status, Stop, Homing, Jog, DirectMove, DriverConfig, Ignored, Error }

        /// <summary>Faithful port of the routing logic in firmware/oapa.ino (dispatchCommand).</summary>
        private static class FirmwareDispatcher {

            public const float DefaultMaxSpeed = 2000;
            public const float JogSpeedMin = 50;
            public const float JogSpeedMax = 3000;

            public static Handler Route(string input) {
                input = input.Trim();
                if (input.Length == 0) return Handler.Ignored;
                if (input[0] == '?') return Handler.Status;
                if (input[0] == '!') return Handler.Stop;
                if (input.StartsWith("$H")) return Handler.Homing;
                if (input.StartsWith("$J=")) {
                    // handleJog: anything without G91/G53 is acknowledged but moves nothing.
                    var spec = input.Substring(3);
                    var relative = spec.Contains("G91");
                    var absolute = spec.Contains("G53");
                    return relative || absolute ? Handler.Jog : Handler.Ignored;
                }
                if (input.Length < 2) return Handler.Error;

                var first = input[0];
                var isAxis = first is 'X' or 'x' or 'Y' or 'y';
                var second = input[1];
                if (isAxis && (char.IsDigit(second) || second == '-')) return Handler.DirectMove;
                if (input.Length > 2 && first is 'C' or 'c' or 'H' or 'h' or 'S' or 's') return Handler.DriverConfig;

                return Handler.Ignored; // firmware replies "ok" and does nothing
            }

            /// <summary>Port of handleDriverConfig's axis/value extraction.</summary>
            public static (char axis, int value) ParseDriverConfig(string input) {
                var axisChar = input[1];
                var axis = axisChar is 'X' or 'x' ? 'X' : axisChar is 'Y' or 'y' ? 'Y' : '?';
                var digits = input.Substring(2);
                int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value);
                return (axis, value);
            }

            /// <summary>Port of jogSpeedFrom (1.2.1): F sets the max speed of that jog, clamped.</summary>
            public static float JogSpeedFrom(string spec) {
                if (!TryReadAxisValue(spec, 'F', out var feed) || feed <= 0) return DefaultMaxSpeed;
                return Math.Clamp(feed, JogSpeedMin, JogSpeedMax);
            }

            /// <summary>Port of readAxisValue: signed decimal following the axis letter.</summary>
            public static bool TryReadAxisValue(string spec, char letter, out float value) {
                value = 0f;
                var at = spec.IndexOf(letter);
                if (at < 0) return false;
                var end = at + 1;
                while (end < spec.Length) {
                    var c = spec[end];
                    if (!char.IsDigit(c) && c != '.' && c != '-') break;
                    end++;
                }
                return float.TryParse(spec.Substring(at + 1, end - at - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
        }

        // --- Commands the plugin actually emits, one test per shape -----------------

        [Test]
        public void StatusProbe_IsRecognized() {
            FirmwareDispatcher.Route("?").Should().Be(Handler.Status);
        }

        [Test]
        public void RelativeJog_AsBuiltByMoveRelative_IsRecognized_AndValueParses() {
            // Mirrors UniversalPolarAlignmentBase.MoveRelative's command construction.
            var commandedSteps = -1234.567f;
            var command = $"$J=G91G21X{commandedSteps.ToString(CultureInfo.InvariantCulture)}F800";

            FirmwareDispatcher.Route(command).Should().Be(Handler.Jog);
            FirmwareDispatcher.TryReadAxisValue(command.Substring(3), 'X', out var parsed).Should().BeTrue();
            parsed.Should().BeApproximately(commandedSteps, 0.001f);
        }

        [Test]
        public void AbsoluteJog_AsBuiltByMoveAbsolute_IsRecognized_AndValueParses() {
            var target = 42.5f;
            var command = $"$J=G53Y{target.ToString(CultureInfo.InvariantCulture)}F800";

            FirmwareDispatcher.Route(command).Should().Be(Handler.Jog);
            FirmwareDispatcher.TryReadAxisValue(command.Substring(3), 'Y', out var parsed).Should().BeTrue();
            parsed.Should().BeApproximately(target, 0.001f);
        }

        [TestCase("X800")]
        [TestCase("Y-200")]
        public void DirectMove_IsRecognized(string command) {
            FirmwareDispatcher.Route(command).Should().Be(Handler.DirectMove);
        }

        [Test]
        public void RunCurrentCommands_AreRecognized_WithCorrectAxisAndValue() {
            foreach (var (axis, letter) in new[] { (Axis.XAxis, 'X'), (Axis.YAxis, 'Y') }) {
                var command = OapaDriverCommands.RunCurrent(axis, 1200);
                FirmwareDispatcher.Route(command).Should().Be(Handler.DriverConfig, because: $"the {letter} run-current command must reach handleDriverConfig");
                FirmwareDispatcher.ParseDriverConfig(command).Should().Be((letter, 1200));
            }
        }

        [Test]
        public void HoldPercentCommands_AreRecognized_WithCorrectAxisAndValue() {
            foreach (var (axis, letter) in new[] { (Axis.XAxis, 'X'), (Axis.YAxis, 'Y') }) {
                var command = OapaDriverCommands.HoldPercent(axis, 40);
                FirmwareDispatcher.Route(command).Should().Be(Handler.DriverConfig, because: $"the {letter} hold-percent command must reach handleDriverConfig");
                FirmwareDispatcher.ParseDriverConfig(command).Should().Be((letter, 40));
            }
        }

        [Test]
        public void Microsteps_ReachTheDriverConfigHandler_ForBothAxes() {
            foreach (var (axis, letter) in new[] { (Axis.XAxis, 'X'), (Axis.YAxis, 'Y') }) {
                var command = OapaDriverCommands.Microsteps(axis, 4);
                FirmwareDispatcher.Route(command).Should().Be(Handler.DriverConfig, because: $"the {letter} microstep command must reach handleDriverConfig");
                FirmwareDispatcher.ParseDriverConfig(command).Should().Be((letter, 4));
            }
        }

        [Test]
        public void StartupBatch_IsFullyRecognized() {
            foreach (var command in OapaDriverCommands.StartupBatch(600, 50, 700, 40, 16, 4)) {
                FirmwareDispatcher.Route(command).Should().Be(Handler.DriverConfig, because: $"'{command}' is pushed on connect and must not be silently ignored");
            }
        }

        [Test]
        public void StopCommand_IsRecognized() {
            FirmwareDispatcher.Route("!").Should().Be(Handler.Stop);
        }

        [Test]
        public void JogSpeed_UsesTheFeedValue_AsSentByTheMoveMethods() {
            FirmwareDispatcher.JogSpeedFrom("G91G21X100F1000").Should().Be(1000f);
            FirmwareDispatcher.JogSpeedFrom("G53Y400F100").Should().Be(100f);
        }

        [Test]
        public void JogSpeed_IsClampedToSafeStepRates() {
            FirmwareDispatcher.JogSpeedFrom("G91G21X100F10").Should().Be(FirmwareDispatcher.JogSpeedMin);
            FirmwareDispatcher.JogSpeedFrom("G91G21X100F999999").Should().Be(FirmwareDispatcher.JogSpeedMax);
        }

        [Test]
        public void JogSpeed_FallsBackToDefault_WhenFeedAbsentOrInvalid() {
            FirmwareDispatcher.JogSpeedFrom("G91G21X100").Should().Be(FirmwareDispatcher.DefaultMaxSpeed);
            FirmwareDispatcher.JogSpeedFrom("G91G21X100F0").Should().Be(FirmwareDispatcher.DefaultMaxSpeed);
        }

        // --- Regression: the historical axis-first grammar is a silent no-op --------

        [TestCase("XC600")]
        [TestCase("YC600")]
        [TestCase("XH50")]
        [TestCase("YH50")]
        public void AxisFirstDriverCommands_AreSilentlyIgnoredByFirmware(string legacyCommand) {
            FirmwareDispatcher.Route(legacyCommand).Should().Be(Handler.Ignored,
                because: "this is the bug: the firmware acknowledges these with 'ok' but changes nothing");
        }
    }
}
