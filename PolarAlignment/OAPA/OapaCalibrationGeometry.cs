using System;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// A single plate-solved sample used by the OAPA self-calibration: the solved
    /// field center plus its topocentric altitude and azimuth at solve time.
    /// </summary>
    public readonly record struct CalibrationSolveSample(double RADegrees, double DecDegrees, double AltitudeDegrees, double AzimuthDegrees);

    /// <summary>
    /// Result of calibrating a single axis from the four-solve leg sequence.
    /// </summary>
    public sealed class AxisCalibrationResult {
        /// <summary>Discovered calibration factor (motor units per arcminute of axis motion).</summary>
        public float Ratio { get; init; }

        /// <summary>
        /// Mechanical backlash measured from the reversal-leg shortfall, in physical axis
        /// arcminutes (clean − reversal). Physical units make the value valid under the
        /// discovered <see cref="Ratio"/> when later converted into compensation moves.
        /// </summary>
        public float BacklashArcmin { get; init; }

        /// <summary>True when the forward leg moved in the topocentric direction the logical command asked for.</summary>
        public bool Consistent { get; init; }

        /// <summary>True when the two backlash-free legs disagree by more than the asymmetry threshold.</summary>
        public bool Asymmetric { get; init; }
    }

    /// <summary>
    /// Pure geometry for the OAPA self-calibration. No hardware, imaging, or UI dependencies,
    /// so every rule (cos-altitude correction, backlash extraction, consistency and asymmetry
    /// checks) is unit-testable in isolation.
    /// </summary>
    public static class OapaCalibrationGeometry {

        /// <summary>Azimuth calibration degenerates as cos(alt) approaches zero; below this the lever is too foreshortened.</summary>
        public const double MinimumAzimuthCosAltitude = 0.25;

        /// <summary>Relative disagreement between the two clean legs above which the measurement is flagged unreliable.</summary>
        public const double AsymmetryThreshold = 0.20;

        /// <summary>Great-circle separation between two solved field centers, in degrees.</summary>
        public static double AngularSeparationDegrees(CalibrationSolveSample a, CalibrationSolveSample b) {
            double ra1 = a.RADegrees * Math.PI / 180.0;
            double ra2 = b.RADegrees * Math.PI / 180.0;
            double dec1 = a.DecDegrees * Math.PI / 180.0;
            double dec2 = b.DecDegrees * Math.PI / 180.0;
            double cosSep = Math.Sin(dec1) * Math.Sin(dec2) + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
            cosSep = Math.Max(-1.0, Math.Min(1.0, cosSep));
            return Math.Acos(cosSep) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Converts a measured sky displacement between two samples into axis displacement, in
        /// arcminutes. For the azimuth axis the sky motion is foreshortened by cos(altitude) of
        /// the observed field — a base rotation of θ moves a field at altitude h by only
        /// θ·cos(h) — so the measurement is divided by cos(mean altitude). The altitude axis
        /// transfers 1:1.
        /// </summary>
        /// <exception cref="InvalidOperationException">The field is too close to the zenith for azimuth calibration.</exception>
        public static double AxisDisplacementArcmin(bool isAzimuthAxis, CalibrationSolveSample from, CalibrationSolveSample to) {
            var skyArcmin = AngularSeparationDegrees(from, to) * 60.0;
            if (!isAzimuthAxis) { return skyArcmin; }

            var meanAlt = (from.AltitudeDegrees + to.AltitudeDegrees) / 2.0;
            var cosAlt = Math.Cos(meanAlt * Math.PI / 180.0);
            if (cosAlt < MinimumAzimuthCosAltitude) {
                throw new InvalidOperationException(
                    $"Field altitude {meanAlt:F0}\u00b0 is too close to the zenith for azimuth calibration. " +
                    "Point the scope at a lower altitude (ideally toward the celestial pole) and retry.");
            }
            return skyArcmin / cosAlt;
        }

        /// <summary>
        /// Whether the observed topocentric displacement between two samples matches the sign
        /// of the logical command that produced it. A positive logical azimuth command must
        /// increase the field's topocentric azimuth and a positive logical altitude command
        /// its topocentric altitude — the convention the correction controller and the manual
        /// nudge buttons assume. A physically reversed axis produces the opposite topocentric
        /// direction, so this check fails on the first pass and succeeds after the Reverse
        /// flag is flipped, which is what makes the auto-flip retry reachable.
        /// </summary>
        public static bool SignedDisplacementMatchesCommand(bool isAzimuthAxis, CalibrationSolveSample from, CalibrationSolveSample to, float logicalCommand) {
            double delta = isAzimuthAxis
                ? WrapDegrees(to.AzimuthDegrees - from.AzimuthDegrees)
                : to.AltitudeDegrees - from.AltitudeDegrees;
            return Math.Sign(delta) == Math.Sign(logicalCommand);
        }

        /// <summary>Maps an angle difference into (−180°, 180°] so azimuth deltas across north keep their sign.</summary>
        private static double WrapDegrees(double degrees) {
            var wrapped = degrees - 360.0 * Math.Round(degrees / 360.0);
            return wrapped == -180.0 ? 180.0 : wrapped;
        }

        /// <summary>
        /// Derives the axis calibration from the measured displacements of the four-solve
        /// sequence: forward (A→B) and reverse (C→D) are single-direction and backlash-free,
        /// yielding the ratio; the reversal leg (B→C) comes up short by exactly the backlash.
        /// </summary>
        /// <param name="commandedArcmin">Commanded size of each leg, in axis arcminutes.</param>
        /// <param name="currentRatio">Calibration factor the commanded moves were issued with.</param>
        /// <exception cref="InvalidOperationException">The axis did not move measurably.</exception>
        public static AxisCalibrationResult ComputeAxisCalibration(
            float commandedArcmin, float currentRatio,
            double forwardArcmin, double reversalArcmin, double reverseArcmin,
            bool directionConsistent,
            Action<string> warn = null) {

            if (forwardArcmin < 0.1 || reverseArcmin < 0.1) {
                throw new InvalidOperationException("Axis did not move measurably; check clutch and motor current");
            }

            var cleanArcmin = (forwardArcmin + reverseArcmin) / 2.0;
            float observedRatio = (float)(currentRatio * (commandedArcmin / cleanArcmin));

            // The reversal leg physically comes up short by exactly the mechanical backlash.
            // Measuring the shortfall directly keeps the value in physical axis arcminutes,
            // valid under the discovered ratio: expressing it via the commanded size would
            // scale it by the obsolete currentRatio and mis-size every later compensation
            // move once the new ratio is applied.
            var backlash = (float)(cleanArcmin - reversalArcmin);
            if (backlash < 0f) {
                backlash = 0f;
            } else if (backlash > cleanArcmin / 2.0) {
                warn?.Invoke($"Measured backlash {backlash:F2}' exceeds half the physical calibration leg; clamping. Check for mechanical slippage.");
                backlash = (float)(cleanArcmin / 2.0);
            }

            // The two clean legs measure the same physical motion; a large mismatch means the
            // measurement itself is unreliable (field drift, flexure, slipping mechanics).
            var asymmetry = Math.Abs(forwardArcmin - reverseArcmin) / Math.Max(forwardArcmin, reverseArcmin);
            bool asymmetric = asymmetry > AsymmetryThreshold;
            if (asymmetric) {
                warn?.Invoke($"Forward ({forwardArcmin:F2}') and reverse ({reverseArcmin:F2}') legs differ by {asymmetry:P0}; discovered values may be unreliable");
            }

            return new AxisCalibrationResult {
                Ratio = observedRatio,
                BacklashArcmin = backlash,
                Consistent = directionConsistent,
                Asymmetric = asymmetric
            };
        }
    }
}
