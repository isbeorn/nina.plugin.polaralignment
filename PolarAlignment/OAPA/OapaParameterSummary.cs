using System;
using System.Globalization;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// One log line per axis, carrying everything needed to read the $J= commands that follow
    /// it. Without this a support log only shows raw step counts, and the effective gear
    /// ratio, backlash, mode and speed have to be recovered by arithmetic.
    /// </summary>
    public static class OapaParameterSummary {

        public static string ForAxis(string axisLabel, double gearRatio, double backlashArcmin, string backlashMode, double speed,
            double backlashNegativeArcmin = double.NaN, int microsteps = 0) {

            var c = CultureInfo.InvariantCulture;
            // Both directions are logged: on a directional axis one figure alone reads as
            // the whole story and the arithmetic of the $J= lines below will not add up.
            var backlash = double.IsNaN(backlashNegativeArcmin) || Math.Abs(backlashNegativeArcmin - backlashArcmin) < 0.005
                ? $"backlash {backlashArcmin.ToString("0.##", c)}'"
                : $"backlash +{backlashArcmin.ToString("0.##", c)}'/-{backlashNegativeArcmin.ToString("0.##", c)}'";
            var line = $"OAPA {axisLabel}: {gearRatio.ToString("0.##", c)} steps/arcmin, " +
                       $"{backlash}, mode {backlashMode}, " +
                       $"speed {speed.ToString("0.##", c)} steps/s";
            if (microsteps > 0) {
                line += $", {microsteps} microsteps";
            }

            // A factor of 1 is the factory default: the platform has never been calibrated,
            // and inventing a reading from it would be worse than showing none (matches
            // UniversalPolarAlignmentOAPAVM.PhysicalSpeed's guard on the same calculation).
            if (gearRatio > 1) {
                var arcminPerSecond = speed / gearRatio;
                line += $" (~ {arcminPerSecond.ToString("0.##", c)} '/s)";
            }

            return line;
        }
    }
}
