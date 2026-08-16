namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// Result of calibrating a single axis from the four-solve leg sequence.
    /// </summary>
    public sealed class AxisCalibrationResult {
        /// <summary>Discovered calibration factor (motor units per arcminute of axis motion).</summary>
        public float Ratio { get; init; }

        /// <summary>
        /// True only when the closing moves verifiably returned the axis to its baseline:
        /// the measured residual came in under the restore tolerance. A calibration whose
        /// measurement succeeded but whose closing failed or left an out-of-tolerance
        /// residual keeps its measured values and reports false here — "measured" and
        /// "physically back at the start" are different claims, and conflating them let a
        /// failed restore be published as full success. Settable because the closing phase
        /// runs after the measured result is assembled.
        /// </summary>
        public bool RestoredToBaseline { get; set; }

        /// <summary>Residual against the baseline after the closing moves, in axis arcminutes; NaN when it could not be measured.</summary>
        public float ClosingResidualArcmin { get; set; } = float.NaN;

        /// <summary>
        /// Mechanical backlash measured from the reversal-leg shortfall, in physical axis
        /// arcminutes (clean − reversal). Physical units make the value valid under the
        /// discovered <see cref="Ratio"/> when later converted into compensation moves.
        /// </summary>
        public float BacklashArcmin { get; init; }

        /// <summary>True when the forward leg moved in the topocentric direction the logical command asked for.</summary>
        public bool Consistent { get; init; }

        /// <summary>True when the clean forward and reverse responses disagree by more than the asymmetry threshold.</summary>
        public bool Asymmetric { get; init; }

        /// <summary>Solve noise measured before any motion (S0), in axis arcminutes.</summary>
        public float NoiseSigmaArcmin { get; init; }

        /// <summary>Calibration factor measured from the clean forward legs alone (reported when <see cref="Asymmetric"/>).</summary>
        public float ForwardRatio { get; init; }

        /// <summary>Calibration factor measured from the clean reverse legs alone (reported when <see cref="Asymmetric"/>).</summary>
        public float ReverseRatio { get; init; }

        /// <summary>
        /// Lost motion when a move in the positive commanded direction reverses into it, in
        /// axis arcminutes. Expressed in commanded (wire) sign, not in the calibration's own
        /// forward/reverse legs, because the Reverse flag inverts the relation between the
        /// two and the consumer of this value plans in wire sign.
        ///
        /// This and <see cref="BacklashEnteringNegativeArcmin"/> are reported as two separate
        /// figures only when <see cref="DirectionalBacklash"/> says the difference between
        /// them is established; otherwise both carry the mean. The distinction matters
        /// because the two-leg reversal plan travels <c>move - outward + back</c>, so any gap
        /// between the pair is a fixed bias on every reversal - real play cancels it, pure
        /// measurement noise does not.
        /// </summary>
        public float BacklashEnteringPositiveArcmin { get; init; }

        /// <summary>Lost motion when a move in the negative commanded direction reverses into it.</summary>
        public float BacklashEnteringNegativeArcmin { get; init; }

        /// <summary>
        /// True when the two backlash transitions disagree beyond noise and tolerance.
        ///
        /// They are two distinct physical quantities, not two samples of one: an axis loaded
        /// by gravity crosses its own play unaided going down and has to be driven across it
        /// going up, so the two can legitimately differ several-fold and still be perfectly
        /// repeatable (field evidence: 53.4'/16.2' and 59.9'/15.9' on two consecutive runs of
        /// the same axis). Their disagreement therefore says the backlash is *directional*,
        /// which is compensable; it says nothing about repeatability, which would need the
        /// same transition measured twice.
        ///
        /// When this is false the two per-direction figures are deliberately reported
        /// <em>equal</em> (both the mean): see <see cref="BacklashEnteringPositiveArcmin"/>.
        /// </summary>
        public bool DirectionalBacklash { get; init; }

        /// <summary>
        /// True when the backlash pair could not be measured meaningfully and both
        /// directions were reported as zero rather than as a plausible-looking number.
        ///
        /// Two ways to get here. A transition that comes out <em>negative</em> before the
        /// clamp means the reversal leg travelled further than the response predicts, which
        /// no amount of real play can produce; and a forward/reverse response disagreement
        /// beyond <see cref="ResponseSuspect"/> makes both transitions garbage, because each
        /// is evaluated against the other direction's response.
        ///
        /// Reporting zero is the conservative failure: an uncompensated reversal under-shoots
        /// and the correction loop recovers on the next cycle, whereas an over-compensated
        /// one injects error the loop then has to fight.
        /// </summary>
        public bool BacklashSuspect { get; init; }

        /// <summary>
        /// True when the clean forward and reverse responses disagree by more than a factor
        /// of two, which makes their mean - the applied <see cref="Ratio"/> - wrong for both
        /// directions rather than a compromise between them. Field evidence: an altitude axis
        /// reporting 0.860 forward against 0.102 reverse produced a factor three times the
        /// one the axis actually delivered during corrections.
        /// </summary>
        public bool ResponseSuspect { get; init; }
    }
}
