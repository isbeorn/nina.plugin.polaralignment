using System;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// A single plate-solved sample used by the OAPA self-calibration: the solved
    /// field center plus its topocentric altitude and azimuth at solve time.
    /// </summary>
    public readonly record struct CalibrationSolveSample(double RADegrees, double DecDegrees, double AltitudeDegrees, double AzimuthDegrees);

    /// <summary>
    /// Pure geometry for the OAPA self-calibration. No hardware, imaging, or UI dependencies,
    /// so the displacement and direction rules are unit-testable in isolation.
    /// </summary>
    public static class OapaCalibrationGeometry {

        /// <summary>
        /// Azimuth calibration degenerates as cos(field altitude) approaches zero — not because the
        /// axis moves the field any less (a base rotation shifts every field's azimuth <em>coordinate</em>
        /// 1:1 at any altitude), but because the solver's on-sky noise inflates by 1/cos(alt) when
        /// expressed in azimuth coordinates. Below this floor the measurement is noise-dominated.
        /// </summary>
        public const double MinimumAzimuthCosAltitude = 0.25;

        /// <summary>
        /// Altitude calibration degenerates as the field approaches due east/west: the altitude
        /// actuator tilts the whole rig about the horizontal east-west axis, so a field at
        /// azimuth A only shows cos(A) of the tilt in its altitude. Near |cos(A)| = 0 the
        /// projection carries no signal, and the noise on what remains inflates by 1/|cos(A)|.
        /// 0.35 admits any field within ~69° of the meridian (or of north) at ~3x noise worst case.
        /// Field evidence for the failure mode this guards: one rig calibrated at A=108-137° across
        /// three sessions and every altitude factor came out inflated by exactly 1/|cos(A)| —
        /// 97.5, 202.7 and 255.0 for a mechanism whose true factor was 73-85 throughout.
        /// </summary>
        public const double MinimumAltitudeCosAzimuth = 0.35;

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
            return Math.Sign(SignedAxisDisplacementArcmin(isAzimuthAxis, from, to)) == Math.Sign(logicalCommand);
        }

        /// <summary>
        /// Signed axis displacement between two samples, in arcminutes of <em>axis motion</em> —
        /// which is also arcminutes of polar-error change, since both TPPA error components are
        /// coordinate differences of the mount axis and the adjusters move that axis 1:1 in
        /// coordinates. That equivalence is what makes a calibrated factor mean "steps per
        /// arcminute of error" regardless of where the calibration happened to point.
        ///
        /// Azimuth: a base rotation about the vertical shifts every field's azimuth coordinate by
        /// exactly the rotation, at any altitude, so the wrapped coordinate delta *is* the axis
        /// motion.
        ///
        /// Altitude: the actuator tilts the rig about the horizontal east-west axis, and a field at
        /// azimuth A shows only cos(A) of that tilt in its altitude — full toward north, full and
        /// sign-reversed toward south, nothing due east/west. Dividing by the <em>signed</em>
        /// cosine recovers the tilt and makes the measured direction independent of which side of
        /// the sky the calibration pointed at, so the Reverse flag stays a property of the wiring
        /// rather than of the pointing. The projection is floored in magnitude at
        /// <see cref="MinimumAltitudeCosAzimuth"/> so restore paths never divide toward zero;
        /// the calibration refuses such pointings up front.
        /// </summary>
        public static double SignedAxisDisplacementArcmin(bool isAzimuthAxis, CalibrationSolveSample from, CalibrationSolveSample to) {
            if (!isAzimuthAxis) {
                var meanAz = from.AzimuthDegrees + WrapDegrees(to.AzimuthDegrees - from.AzimuthDegrees) / 2.0;
                var projection = Math.Cos(meanAz * Math.PI / 180.0);
                var floored = projection >= 0
                    ? Math.Max(projection, MinimumAltitudeCosAzimuth)
                    : Math.Min(projection, -MinimumAltitudeCosAzimuth);
                return (to.AltitudeDegrees - from.AltitudeDegrees) * 60.0 / floored;
            }
            return WrapDegrees(to.AzimuthDegrees - from.AzimuthDegrees) * 60.0;
        }

        /// <summary>Maps an angle difference into (−180°, 180°] so azimuth deltas across north keep their sign.</summary>
        private static double WrapDegrees(double degrees) {
            var wrapped = degrees - 360.0 * Math.Round(degrees / 360.0);
            return wrapped == -180.0 ? 180.0 : wrapped;
        }

    }
}
