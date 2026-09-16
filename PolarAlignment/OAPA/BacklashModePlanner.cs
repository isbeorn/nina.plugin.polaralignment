using System;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// How an OAPA axis handles its mechanical backlash on direction reversals,
    /// from the most conservative to the most aggressive strategy.
    /// </summary>
    public enum OapaBacklashMode {
        /// <summary>No compensation: plain moves (negligible measured backlash).</summary>
        Off,
        /// <summary>Single move extended by 75% of the backlash: limits the injected error when the value is overestimated.</summary>
        Soft,
        /// <summary>Single move extended by the full backlash: the engagement is part of the move, no out-and-back excursion.</summary>
        Full,
        /// <summary>Overshoot past the target and return so the final approach always comes from the engaged direction.</summary>
        Unidirectional
    }

    /// <summary>
    /// Pure move planning for the OAPA backlash modes and the mode recommendation rule.
    /// The invariant of every reversal plan is the physical arrival point: under a
    /// mechanics that loses exactly the configured backlash per reversal, the axis lands
    /// on the requested target (Soft deliberately trades up to B/4 of that for safety
    /// against an overestimated value).
    ///
    /// The play is configured per direction, because entering one direction and entering
    /// the other are two different physical quantities: an axis carrying its load against
    /// gravity crosses its own play unaided one way and has to be driven across it the
    /// other, so the two can differ several-fold on the same axis. Every leg is therefore
    /// compensated with the value of the direction *that leg* travels, which is what makes
    /// a two-leg plan land exactly: the overshoot leg pays one transition and the return
    /// leg pays the other, and they cancel only when each is given its own number. Passing
    /// one value for both directions reproduces the symmetric behaviour exactly.
    ///
    /// The cost of that exactness is a sharp dependence on the *difference* between the two
    /// numbers, which the single-value form does not have. A Unidirectional plan commands a
    /// net travel of <c>move - outward + back</c>; the missing part is supposed to be eaten
    /// by real play, so if the mechanism's play is smaller than configured, the gap between
    /// the pair lands as a fixed bias on every reversal. It follows that the axis cannot be
    /// corrected by less than that gap, and that requests below it move the axis the *wrong
    /// way*. Two field rigs stalled at exactly their configured gap, 9.3' and 7.3'. A wrong
    /// difference is therefore worse than a wrong average, and the pair must only be split
    /// when the difference is established - which is what the calibration's directionality
    /// verdict decides before the values ever reach here.
    /// </summary>
    public static class BacklashModePlanner {

        /// <summary>Share of the backlash applied by the Soft mode.</summary>
        private const float SoftFraction = 0.75f;
        /// <summary>Unidirectional overshoot beyond the backlash: 25% of it plus a fixed floor, absorbing estimate errors.</summary>
        private const float OvershootFractionOfBacklash = 0.25f;
        private const float OvershootFloorArcmin = 0.5f;

        /// <summary>Backlash below max(2 sigma, floor) is indistinguishable from solve noise.</summary>
        private const float MeasurableSigmaFactor = 2f;
        private const float MeasurableFloorArcmin = 0.5f;
        /// <summary>Above this, the compensation error itself is significant: approach from one side only.</summary>
        private const float LargeBacklashArcmin = 3f;

        /// <summary>
        /// The smallest reversal this axis can be asked for and still arrive where it was
        /// sent. A compensated plan lands exactly only in so far as the configured play
        /// matches the mechanism's real play, and that agreement is only known to the
        /// precision the calibration could measure it with - which is the same
        /// <see cref="MeasurableFloorArcmin"/> below which a backlash is declared
        /// unmeasurable in the first place. Ask for less than that across a reversal and the
        /// compensation's own error is larger than the correction being attempted.
        ///
        /// Field case: a 0.19' correction on an axis compensating 0.68' was commanded as
        /// 0.87', tripled the error, and did it four times in a row before the alignment gave
        /// up and finished best-effort - at which point a fresh measurement found 17'34" where
        /// the loop was claiming 0.38'.
        ///
        /// Zero for <see cref="OapaBacklashMode.Off"/> and for any move continuing in the
        /// engaged direction: those pay no play, so they are exact at any size, which is why
        /// an axis with no measurable backlash keeps converging as finely as the solver allows.
        /// </summary>
        /// <remarks>
        /// Supplied by the caller rather than assumed here, because the planner does not know
        /// how well the numbers it is given were measured. The field replay suite settles the
        /// question: under a mechanism that loses exactly the configured play, small reversals
        /// land exactly and are *needed* to converge - blocking them on principle stops healthy
        /// rigs short of tolerance. So the floor belongs to whoever knows the calibration's own
        /// precision, and is zero by default.
        /// </remarks>

        /// <summary>
        /// Plans the command sequence for a requested relative move on an axis whose play
        /// costs the same in both directions.
        /// </summary>
        public static float[] PlanMoves(OapaBacklashMode mode, float move, float backlashArcmin, LastDirection lastDirection)
            => PlanMoves(mode, move, backlashArcmin, backlashArcmin, lastDirection);

        /// <summary>
        /// Plans the command sequence for a requested relative move. Non-reversing moves
        /// (and reversals with no configured backlash, or mode Off) are passed through.
        /// </summary>
        /// <param name="backlashEnteringPositive">Motion lost when a move in the positive direction reverses into it.</param>
        /// <param name="backlashEnteringNegative">Motion lost when a move in the negative direction reverses into it.</param>
        /// <param name="minimumReversalArcmin">
        /// Smallest reversal this axis can be asked for and still arrive where it was sent;
        /// requests below it return an empty plan. Zero disables the check.
        /// </param>
        public static float[] PlanMoves(OapaBacklashMode mode, float move,
            float backlashEnteringPositive, float backlashEnteringNegative, LastDirection lastDirection,
            float minimumReversalArcmin = 0f) {

            var sign = Math.Sign(move);
            var lastSign = lastDirection == LastDirection.Positive ? 1 : -1;

            if (sign == 0 || mode == OapaBacklashMode.Off) {
                return new[] { move };
            }

            // A reversal finer than the compensation is known to cannot be honoured:
            // compensating overshoots it, not compensating loses it to the play. An empty plan
            // says so - moving the axis badly is worse than leaving it where it is, because
            // the caller reads the same error again and asks again, which is how a correction
            // loop ends up oscillating instead of converging. Only reversals are subject to
            // this: a move continuing in the engaged direction pays no play at any size.
            var hasPlay = Math.Max(backlashEnteringPositive, backlashEnteringNegative) > 0f;
            var reverses = mode == OapaBacklashMode.Unidirectional
                ? sign < 0 || lastSign < 0
                : sign != lastSign;
            if (hasPlay && reverses && minimumReversalArcmin > 0f && Math.Abs(move) < minimumReversalArcmin) {
                return Array.Empty<float>();
            }

            if (mode == OapaBacklashMode.Unidirectional) {
                // True unidirectional approach: EVERY move arrives travelling in the
                // positive direction, and the axis rests engaged positive afterwards.
                // On the altitude axis positive is "up", so the gears are always loaded
                // against gravity at rest and the mechanism cannot settle into its own
                // slack after a move (a tester watched exactly that creep). It also makes
                // the arrival mechanically identical every time - same flank, same
                // direction - so a compensation error becomes a constant offset the
                // adaptive controller can learn, instead of a sign-alternating one it
                // cannot. The previous behaviour had two stable regimes (approach-up and
                // approach-down) selected by history: an interrupted plan or a manual
                // nudge could flip a rig into the mirrored regime and keep it there.
                if (backlashEnteringPositive <= 0f && backlashEnteringNegative <= 0f) {
                    return new[] { move }; // no measurable play: nothing to protect
                }
                if (sign > 0) {
                    if (lastSign > 0) {
                        return new[] { move }; // already approaching positively
                    }
                    // Engaged negative (interrupted plan, manual nudge): one compensated
                    // leg re-engages and arrives travelling positive.
                    return backlashEnteringPositive > 0f
                        ? new[] { move + backlashEnteringPositive }
                        : new[] { move };
                }
                // Negative move: overshoot below the target, then arrive from underneath.
                // The overshoot cancels between the legs whatever its size, so it is sized
                // off the larger transition purely as margin against an underestimate; the
                // outward leg pays the entering-negative play only when it actually
                // reverses into it, and the return leg always pays the entering-positive one.
                var uniOvershoot = OvershootFractionOfBacklash * Math.Max(Math.Max(backlashEnteringPositive, backlashEnteringNegative), 0f) + OvershootFloorArcmin;
                var reversalPlay = lastSign > 0 ? Math.Max(0f, backlashEnteringNegative) : 0f;
                return new[] {
                    move - reversalPlay - uniOvershoot,
                    Math.Max(0f, backlashEnteringPositive) + uniOvershoot
                };
            }

            // Soft and Full compensate only actual reversals, in the move's own direction.
            var outward = sign > 0 ? backlashEnteringPositive : backlashEnteringNegative;
            if (sign == lastSign || outward <= 0f) {
                return new[] { move };
            }

            switch (mode) {
                case OapaBacklashMode.Soft:
                    return new[] { move + sign * SoftFraction * outward };
                case OapaBacklashMode.Full:
                    return new[] { move + sign * outward };
                default:
                    return new[] { move };
            }
        }

        /// <summary>
        /// Recommends a mode from the calibration measurements: not measurable -> Off;
        /// small -> Full; large -> Unidirectional. Soft stays a manual, conservative choice.
        /// The threshold is about the *size* of the play, not its symmetry: a directional
        /// play is compensated exactly by the per-direction plan, so it does not change
        /// which mode is appropriate.
        /// </summary>
        public static OapaBacklashMode Recommend(float backlashArcmin, float noiseSigmaArcmin) {
            var measurable = Math.Max(MeasurableSigmaFactor * noiseSigmaArcmin, MeasurableFloorArcmin);
            if (backlashArcmin < measurable) {
                return OapaBacklashMode.Off;
            }
            return backlashArcmin <= LargeBacklashArcmin ? OapaBacklashMode.Full : OapaBacklashMode.Unidirectional;
        }

        /// <summary>
        /// Recommends from a per-direction measurement. The worse direction decides: a mode
        /// safe for the cheaper transition is not necessarily safe for the expensive one.
        /// </summary>
        public static OapaBacklashMode Recommend(float backlashEnteringPositive, float backlashEnteringNegative, float noiseSigmaArcmin)
            => Recommend(Math.Max(backlashEnteringPositive, backlashEnteringNegative), noiseSigmaArcmin);
    }
}
