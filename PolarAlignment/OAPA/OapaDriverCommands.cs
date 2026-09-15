using System;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// Builds the TMC driver configuration commands in the grammar the firmware parses:
    /// type letter first (C = run current in mA, H = hold percent, S = microsteps), then
    /// the axis letter, then the value — e.g. "CX600", "HY50", "SY4". The firmware silently
    /// ignores any other shape, so this class is the single owner of the wire format.
    /// </summary>
    public static class OapaDriverCommands {

        private static char AxisLetter(Axis axis) => axis switch {
            Axis.XAxis => 'X',
            Axis.YAxis => 'Y',
            _ => throw new ArgumentException($"No driver configuration for axis {axis}"),
        };

        public static string RunCurrent(Axis axis, int milliamps) => $"C{AxisLetter(axis)}{milliamps}";

        public static string HoldPercent(Axis axis, int percent) => $"H{AxisLetter(axis)}{percent}";

        public static string Microsteps(Axis axis, int microsteps) => $"S{AxisLetter(axis)}{microsteps}";

        /// <summary>
        /// The full driver configuration pushed once after connect, so the stored settings
        /// take effect without requiring the user to re-edit each field. The firmware does
        /// not persist driver settings across power cycles — including the microstep
        /// setting, which resets to the firmware default and would leave every commanded
        /// move wrong by that ratio if it were not pushed here.
        ///
        /// Microsteps go first: they change what a step means, so the axis reaches its final
        /// resolution before anything can move.
        /// </summary>
        public static string[] StartupBatch(int xRunMA, int xHoldPercent, int yRunMA, int yHoldPercent,
            int xMicrosteps, int yMicrosteps) => new[] {
            Microsteps(Axis.XAxis, xMicrosteps),
            Microsteps(Axis.YAxis, yMicrosteps),
            RunCurrent(Axis.XAxis, xRunMA),
            HoldPercent(Axis.XAxis, xHoldPercent),
            RunCurrent(Axis.YAxis, yRunMA),
            HoldPercent(Axis.YAxis, yHoldPercent),
        };
    }
}
