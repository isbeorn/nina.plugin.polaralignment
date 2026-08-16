using NINA.Core.Utility;
using System;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// The measured quantities of one axis calibration pass, as delivered by the staged
    /// leg sequence. Everything here is a number read off the sky (or the command that
    /// produced it): the derivation into verdicts happens in <see cref="OapaCalibrationVerdicts"/>.
    /// </summary>
    /// <param name="CurrentRatio">Calibration factor in effect while the pass was measured (motor units per arcminute).</param>
    /// <param name="DirSign">+1 when the Reverse flag was off during the pass, -1 when it was on.</param>
    /// <param name="NoiseSigmaArcmin">Solve noise measured before any motion (S0), in axis arcminutes.</param>
    /// <param name="DetectionThresholdArcmin">Motion detection threshold derived from the noise, in axis arcminutes.</param>
    /// <param name="DirectionConsistent">Whether the first clean forward leg moved the topocentric direction the logical command asked for.</param>
    /// <param name="ForwardResponse">Clean forward response, in axis arcminutes per logical unit (S2).</param>
    /// <param name="ReverseResponse">Clean reverse response, in axis arcminutes per logical unit (S4).</param>
    /// <param name="BacklashLegArcmin">Size of the (possibly escalated) backlash leg, in logical arcminutes.</param>
    /// <param name="ReversalTravelArcmin">Unsigned travel measured on the S3 reversal transition, in axis arcminutes.</param>
    /// <param name="OppositeTravelArcmin">Unsigned travel measured on the S5 opposite transition, in axis arcminutes.</param>
    public readonly record struct AxisCalibrationMeasurements(
        float CurrentRatio,
        float DirSign,
        double NoiseSigmaArcmin,
        double DetectionThresholdArcmin,
        bool DirectionConsistent,
        double ForwardResponse,
        double ReverseResponse,
        float BacklashLegArcmin,
        double ReversalTravelArcmin,
        double OppositeTravelArcmin);

    /// <summary>
    /// The derived verdicts plus the raw clamped transition pair they were derived from.
    /// The raw pair differs from the applied pair on the result whenever the
    /// directionality verdict collapses them — a log that only showed one of the two is
    /// what made the phantom-split failure take three field sessions to find.
    /// </summary>
    public sealed class AxisVerdictDerivation {
        public AxisCalibrationResult Result { get; init; }
        /// <summary>Clamped backlash transition measured entering the leg direction -DirSign (S3), in axis arcminutes.</summary>
        public double BacklashForwardArcmin { get; init; }
        /// <summary>Clamped backlash transition measured entering the leg direction +DirSign (S5), in axis arcminutes.</summary>
        public double BacklashReverseArcmin { get; init; }
    }

    /// <summary>
    /// Pure derivation of the calibration verdicts from the measured quantities of one
    /// axis pass. No hardware, imaging, or UI dependencies: every rule that decides what
    /// the measurements mean — the suspect flags, the directionality verdict, the scale
    /// choice — lives here, unit-testable against the field cases that motivated it.
    /// </summary>
    public static class OapaCalibrationVerdicts {

        /// <summary>Forward/reverse response disagreement above which the axis is flagged asymmetric.</summary>
        public const double AsymmetryFlagThreshold = 0.10;
        /// <summary>Below this min/max response ratio (a factor of two) the pass measured nothing usable.</summary>
        public const double ResponseAgreementFloor = 0.5;
        /// <summary>Backlash-transition disagreement share above which the play is declared direction-dependent.</summary>
        public const double DirectionalRelativeThreshold = 0.20;

        public static AxisVerdictDerivation Derive(AxisCalibrationMeasurements m, string axisLabel) {
            // An axis where neither direction produced measurable motion has measured no
            // scale at all, so there is no factor to report - only a failed pass. Same
            // condition the engagement probe already fails on, found one stage later.
            if (!IsUsableResponse(m.ForwardResponse) && !IsUsableResponse(m.ReverseResponse)) {
                throw new InvalidOperationException(
                    $"{axisLabel}: neither direction produced measurable motion ({m.ForwardResponse:F3}/{m.ReverseResponse:F3} '/unit); " +
                    "check the clutch, the motor current and the speed of this axis");
            }

            // The backlash transitions are evaluated against the response of the
            // direction the axis was travelling toward, so a direction asymmetry does
            // not masquerade as backlash (or as slippage).
            //
            // The raw (unclamped) value is kept: a *negative* transition means the
            // reversal leg travelled further than the response predicts, which no amount
            // of real play can produce. Clamping it to zero turns that impossibility into
            // a plausible-looking "no play this way", and paired against a significant
            // value in the other direction that zero becomes tens of arcminutes of
            // compensation made of nothing.
            var rawForward = m.BacklashLegArcmin * m.ReverseResponse - m.ReversalTravelArcmin;
            var backlashForward = Math.Max(0, rawForward);

            // The opposite transition is a *second quantity*, not a second sample of the
            // first: the two are equal only on a mechanism whose play costs the same to
            // cross both ways, which an axis carrying its load against gravity is not.
            // Their disagreement is therefore a directionality verdict.
            var rawReverse = m.BacklashLegArcmin * m.ForwardResponse - m.OppositeTravelArcmin;
            var backlashReverse = Math.Max(0, rawReverse);

            var maxResponse = Math.Max(m.ForwardResponse, m.ReverseResponse);
            var responseAgreement = maxResponse > 0 ? Math.Min(m.ForwardResponse, m.ReverseResponse) / maxResponse : 0.0;
            var asymmetric = 1.0 - responseAgreement > AsymmetryFlagThreshold;
            // Beyond a factor of two the mean is not a compromise between the two
            // directions, it is wrong for both - and each backlash transition is
            // evaluated against the *other* direction's response, so the pair goes with
            // it. Field evidence: fwd=0.860 against rev=0.102 produced a factor three
            // times what the axis delivered during the corrections that followed.
            //
            // A direction that produced no motion at all is stated separately rather than
            // left to the agreement arithmetic: it is the same verdict for a stronger
            // reason, and saying so explicitly keeps it true no matter how the ratio of
            // two degenerate numbers happens to come out.
            var responseSuspect = !IsUsableResponse(m.ForwardResponse)
                                  || !IsUsableResponse(m.ReverseResponse)
                                  || responseAgreement < ResponseAgreementFloor;

            double backlash;
            var directional = false;
            var backlashSuspect = responseSuspect;
            var significant = Math.Max(backlashForward, backlashReverse);
            if (backlashSuspect || Math.Min(rawForward, rawReverse) < -m.DetectionThresholdArcmin) {
                // An impossible transition invalidates the pair, not just itself: both
                // are computed from the same two responses over the same escalated leg.
                //
                // This is tested before the noise floor because `significant` is built
                // from the *clamped* values: an impossible -5' whose partner also clamps
                // to zero leaves it at zero, and the pair would otherwise be dismissed as
                // "both below the noise" - reporting a clean "no play" for a pass that
                // measured something physically impossible.
                backlashSuspect = true;
                backlash = 0;
            } else if (significant < 2 * m.DetectionThresholdArcmin) {
                backlash = 0; // both transitions indistinguishable from noise
            } else {
                // A transition indistinguishable from zero paired against a significant
                // one cannot establish directionality. Zero-against-large is the field
                // signature of a slipped measurement, not of directional mechanics: the
                // same axis measured 4.10'/4.31' and, five minutes later, 0.00'/8.69' -
                // stable sum, flipped split - and the phantom pair threw a 23" residual
                // to 6'32" at the finish line. (0.00'/27.21' on the same rig is the other
                // occurrence; no genuine pair with a zero side has ever repeated.) The
                // mean is the safe collapse: a symmetric value's magnitude cancels out of
                // the two-leg plan, so even an imperfect mean only costs travel time.
                var bothTransitionsMeasurable = Math.Min(backlashForward, backlashReverse) >= m.DetectionThresholdArcmin;
                directional = bothTransitionsMeasurable
                    && Math.Abs(backlashForward - backlashReverse) > Math.Max(DirectionalRelativeThreshold * significant, 2 * m.DetectionThresholdArcmin);
                backlash = (backlashForward + backlashReverse) / 2.0;
                if (!bothTransitionsMeasurable) {
                    Logger.Info($"OAPA cal {axisLabel}: one backlash transition is indistinguishable from zero against " +
                        $"{significant:F2}' on the other - the split is not established (slip signature); using the mean {backlash:F2}' for both directions");
                }
            }

            // backlashForward was measured entering the leg direction -DirSign (S3),
            // backlashReverse entering +DirSign (S5). The Reverse flag therefore swaps
            // which one belongs to which commanded sign; resolving it here means no
            // consumer has to know about DirSign at all.
            var enteringPositive = (float)(m.DirSign > 0 ? backlashReverse : backlashForward);
            var enteringNegative = (float)(m.DirSign > 0 ? backlashForward : backlashReverse);
            if (!directional) {
                // A "not directional" verdict is the statement that these two figures are
                // the same quantity measured twice. Reporting them separately anyway hands
                // the planner a difference made of measurement noise, and a two-leg
                // reversal travels `move - outward + back`: that gap becomes a fixed bias
                // on every reversal, so the axis can never be corrected by less than the
                // gap and requests below it move it the wrong way. Two field rigs stalled
                // at exactly their own gap - 9.3' and 7.3' - with `directional=false` in
                // the same log line. Collapsing to the mean restores the single-value
                // behaviour wherever the difference is not established, and changes
                // nothing where it is.
                enteringPositive = enteringNegative = (float)backlash;
            }

            var meanResponse = (m.ForwardResponse + m.ReverseResponse) / 2.0;

            // A factor error is a scale: it affects both directions identically. Two
            // responses that disagree by more than the agreement floor are therefore
            // not two measurements of the scale - the weaker direction is losing
            // motion mechanically (stall, slip, insufficient torque) and blending it
            // in poisons the factor. Field case: responses 0.199/0.958 blended into a
            // factor 1.7x too large, and recalibrating on top of that compounded it to
            // 3.6x, while the strong direction alone was within a few percent of the
            // truth. When the pair is suspect, the strong direction IS the scale.
            // Math.Max propagates NaN, and the stronger direction is the one being trusted
            // here, so the pick has to skip a direction that measured nothing rather than
            // let it poison the scale.
            var scaleResponse = responseSuspect ? StrongerUsableResponse(m) : meanResponse;

            var ratio = (float)(m.CurrentRatio / scaleResponse);
            if (!float.IsFinite(ratio)) {
                throw new InvalidOperationException(
                    $"{axisLabel}: the measured response does not yield a usable calibration factor " +
                    $"({m.ForwardResponse:F3}/{m.ReverseResponse:F3} '/unit)");
            }

            var result = new AxisCalibrationResult {
                Ratio = ratio,
                ForwardRatio = PerDirectionRatio(m.CurrentRatio, m.ForwardResponse),
                ReverseRatio = PerDirectionRatio(m.CurrentRatio, m.ReverseResponse),
                BacklashArcmin = (float)backlash,
                NoiseSigmaArcmin = (float)m.NoiseSigmaArcmin,
                Consistent = m.DirectionConsistent,
                Asymmetric = asymmetric,
                BacklashEnteringPositiveArcmin = enteringPositive,
                BacklashEnteringNegativeArcmin = enteringNegative,
                DirectionalBacklash = directional,
                BacklashSuspect = backlashSuspect,
                ResponseSuspect = responseSuspect
            };

            return new AxisVerdictDerivation {
                Result = result,
                BacklashForwardArcmin = backlashForward,
                BacklashReverseArcmin = backlashReverse
            };
        }

        /// <summary>
        /// Whether a measured response can be divided by. Zero means the direction did not
        /// move; NaN and infinity mean the measurement itself failed. All three are the
        /// same statement: this direction measured nothing.
        /// </summary>
        private static bool IsUsableResponse(double response) {
            return response > 0 && !double.IsInfinity(response);
        }

        /// <summary>The stronger of the two responses, ignoring one that measured nothing.</summary>
        private static double StrongerUsableResponse(AxisCalibrationMeasurements m) {
            if (!IsUsableResponse(m.ForwardResponse)) { return m.ReverseResponse; }
            if (!IsUsableResponse(m.ReverseResponse)) { return m.ForwardResponse; }
            return Math.Max(m.ForwardResponse, m.ReverseResponse);
        }

        /// <summary>
        /// The factor a single direction measured, or NaN when that direction produced no
        /// motion. Dividing by a zero response yields infinity, and a plausible-looking
        /// substitute would be worse: these two values are reported to the user in the
        /// calibration summary, and a direction that measured nothing must read as nothing
        /// rather than as a number someone could act on.
        /// </summary>
        private static float PerDirectionRatio(float currentRatio, double response) {
            if (!IsUsableResponse(response)) {
                return float.NaN;
            }
            var ratio = (float)(currentRatio / response);
            return float.IsFinite(ratio) ? ratio : float.NaN;
        }
    }
}
