using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Replays the three field sessions that produced the per-direction backlash values, with
    /// the mechanism simulated separately from the configuration so the two can disagree -
    /// which is the whole failure mode and the one thing a plan-shape assertion cannot see.
    ///
    /// The arithmetic these pin, for a Unidirectional reversal of magnitude m planned from a
    /// configured pair (P, N) against a mechanism whose real play is (Lp, Ln):
    ///
    ///     arrival = -m + (P - N) - (Lp - Ln)
    ///
    /// Two consequences decide everything here. The plan is <em>insensitive to the magnitude</em>
    /// of the play - P and N cancel against themselves, so an overestimate costs travel time
    /// and nothing else - and <em>exactly as sensitive to the difference</em>, which does not
    /// cancel against anything. A symmetric pair therefore lands on target whatever the
    /// mechanism does; an asymmetric one lands on target only if its gap is the real gap.
    ///
    /// Both rigs that stalled in the field had a gap the calibration had already judged
    /// unestablished (directional=false in the same log line), and both stalled at exactly
    /// their own gap.
    /// </summary>
    public class OapaDirectionalBacklashFieldReplayTest {

        /// <summary>
        /// Applies a plan to a mechanism with per-direction lost motion, starting engaged in
        /// <paramref name="lastDirection"/>, and returns the net physical displacement.
        /// </summary>
        private static float Arrive(float[] plan, float realEnteringPositive, float realEnteringNegative,
            LastDirection lastDirection = LastDirection.Positive) {

            var position = 0f;
            var lastSign = lastDirection == LastDirection.Positive ? 1 : -1;
            foreach (var move in plan) {
                var sign = System.Math.Sign(move);
                if (sign == 0) { continue; }
                var lost = sign != lastSign ? (sign > 0 ? realEnteringPositive : realEnteringNegative) : 0f;
                position += move - sign * System.Math.Min(System.Math.Abs(move), lost);
                lastSign = sign;
            }
            return position;
        }

        private static float[] Plan(float move, float configuredPositive, float configuredNegative) =>
            BacklashModePlanner.PlanMoves(OapaBacklashMode.Unidirectional, move,
                configuredPositive, configuredNegative, LastDirection.Positive);

        // ---------------------------------------------------------------- the two invariants

        [Test]
        public void SymmetricPair_LandsExactly_WhateverPlayTheMechanismActuallyHas() {
            // The property that makes the mean safe: B cancels against itself on both legs,
            // so the arrival does not depend on how much of it the mechanism really loses.
            // An overestimated symmetric value costs excursion time, never accuracy.
            foreach (var configured in new[] { 5f, 20f, 50f }) {
                foreach (var real in new[] { 0f, 1f, 12f, 50f }) {
                    if (real > configured) { continue; }
                    Arrive(Plan(-15f, configured, configured), real, real)
                        .Should().BeApproximately(-15f, 0.01f, $"configured={configured}, real={real}");
                }
            }
        }

        [Test]
        public void AsymmetricPair_MissesByExactlyTheDifferenceOfTheDifferences() {
            // configured gap 20, real gap 5 -> the plan is 15' long on every reversal,
            // whatever the sizes involved.
            Arrive(Plan(-15f, configuredPositive: 30f, configuredNegative: 10f),
                   realEnteringPositive: 15f, realEnteringNegative: 10f)
                .Should().BeApproximately(-15f + 20f - 5f, 0.01f);

            // And it is exact the moment the configured gap is the real gap, whatever the
            // absolute values: this is the case the per-direction pair exists for.
            Arrive(Plan(-15f, configuredPositive: 30f, configuredNegative: 10f),
                   realEnteringPositive: 25f, realEnteringNegative: 5f)
                .Should().BeApproximately(-15f, 0.01f);
        }

        // ---------------------------------------------------------------- rig A, altitude

        // Applied 2026-08-08 21:40: Y +57.16'/-49.78', Unidirectional, directional=false.
        private const float RigA_Positive = 57.16f;
        private const float RigA_Negative = 49.78f;

        [Test]
        public void RigA_ThirtyArcminuteRequest_DeliversTwentyTwo_WhenTheAxisLosesNoPlay() {
            // Field: a -30' request moved the altitude error by 22'35". The zero-play
            // prediction of the plan is 22.62' - the axis followed the commanded sum, which
            // is what says the 50-57' the calibration measured is not there.
            Arrive(Plan(-30f, RigA_Positive, RigA_Negative), 0f, 0f)
                .Should().BeApproximately(-22.62f, 0.05f);
        }

        [Test]
        public void RigA_TenArcminuteRequests_AreSwallowedByTheGap() {
            // Field: four consecutive -10.1'..-10.4' requests delivered 26", 36", 54", 1'53".
            // The plan's own floor accounts for the shortfall without invoking stiction.
            Arrive(Plan(-10.36f, RigA_Positive, RigA_Negative), 0f, 0f)
                .Should().BeApproximately(-(10.36f - 7.38f), 0.05f);
        }

        // ---------------------------------------------------------------- rig B, azimuth

        // Applied 2026-08-08 23:02: X +54.34'/-45.02', Unidirectional, directional=false.
        private const float RigB_Positive = 54.34f;
        private const float RigB_Negative = 45.02f;
        // Applied 2026-08-08 23:25 after a second calibration: the gap changes sign.
        private const float RigB2_Positive = 38.73f;
        private const float RigB2_Negative = 46.06f;

        [Test]
        public void RigB_CorrectionsStallAtTheConfiguredGap_AndReverseBelowIt() {
            // The log walks straight down to the crossing and then stops improving:
            //   -9.68' -> 0.3'   -9.49' -> 0.13'   -9.33' -> 0.07'   -9.22' -> -0.03'
            // 54.34 - 45.02 = 9.32, so a request of exactly 9.32' commands zero net travel.
            var gap = RigB_Positive - RigB_Negative;
            gap.Should().BeApproximately(9.32f, 0.01f);

            Arrive(Plan(-gap, RigB_Positive, RigB_Negative), 0f, 0f)
                .Should().BeApproximately(0f, 0.01f, "the axis cannot be corrected by less than the gap");

            Arrive(Plan(-9.16f, RigB_Positive, RigB_Negative), 0f, 0f)
                .Should().BePositive("below the gap the plan drives the axis away from the target");
        }

        [Test]
        public void RigB_AfterTheSecondCalibration_TheGapFlipsSign_AndSmallRequestsOvershoot() {
            // Field: a -1.18' request swung the azimuth error from -3'20" to +3'26".
            // 38.73 - 46.06 = -7.33, so the plan commands -8.51' for a -1.18' request.
            Arrive(Plan(-1.18f, RigB2_Positive, RigB2_Negative), 0f, 0f)
                .Should().BeApproximately(-8.51f, 0.05f);
        }

        // ---------------------------------------------------------------- rig C, azimuth

        [Test]
        public void RigC_GenuinelyDirectionalAxis_StillLandsExactly() {
            // The rig the pair was built for: three calibrations, each transition repeating
            // to within 13% of itself and the two differing by 1.8x. Here the configured gap
            // *is* the real gap, so the plan is exact - and the fine phase closes instead of
            // oscillating. Nothing in this release may take that away.
            foreach (var move in new[] { -0.23f, -2f, -15f }) {
                Arrive(Plan(move, configuredPositive: 2.63f, configuredNegative: 4.01f),
                       realEnteringPositive: 2.63f, realEnteringNegative: 4.01f)
                    .Should().BeApproximately(move, 0.01f, $"move={move}");
            }
        }

        // ---------------------------------------------------------------- what the fix buys

        [Test]
        public void CollapsingAnUnestablishedPairToItsMean_RemovesTheStallOnEveryRig() {
            // Same three configurations, each replaced by its own mean - which is what the
            // calibration now reports when it has judged the difference unestablished. The
            // arrival is the request on every rig and for every amount of real play,
            // including none: no floor, no sign reversal, nothing to stall against.
            var rigs = new[] {
                (name: "rig A altitude", p: RigA_Positive, n: RigA_Negative),
                (name: "rig B azimuth run 1", p: RigB_Positive, n: RigB_Negative),
                (name: "rig B azimuth run 2", p: RigB2_Positive, n: RigB2_Negative),
            };

            foreach (var rig in rigs) {
                var mean = (rig.p + rig.n) / 2f;
                foreach (var move in new[] { -30f, -10f, -9.32f, -1.18f, -0.3f }) {
                    foreach (var realPlay in new[] { 0f, 5f, 40f }) {
                        Arrive(Plan(move, mean, mean), realPlay, realPlay)
                            .Should().BeApproximately(move, 0.01f, $"{rig.name}, move={move}, real play={realPlay}");
                    }
                }
            }
        }

        [Test]
        public void StoredPairsFromThePreviousRelease_AreResetToSymmetric() {
            // Settings outlive the code that wrote them: a rig that upgrades and does not
            // re-calibrate would otherwise keep driving with the gap that stalled it, because
            // the fix lives in the calibration and the calibration is not run on upgrade.
            // The verdict that produced the stored pair is gone, so the only honest move is
            // back to symmetric - which is exactly the behaviour of the release before the
            // pair existed.
            var settings = Properties.Settings.Default;
            settings.OAPABacklashPairSchema = 0;
            settings.OAPAXBacklashCompensation = RigB_Positive;
            settings.OAPAXBacklashCompensationNegative = RigB_Negative;
            settings.OAPAYBacklashCompensationNegative = RigA_Negative;

            OapaSettingsMigration.EnsureCurrent();

            settings.OAPAXBacklashCompensationNegative.Should().Be(-1f, "-1 means 'not set', i.e. same as the positive direction");
            settings.OAPAYBacklashCompensationNegative.Should().Be(-1f);
            settings.OAPAXBacklashCompensation.Should().Be(RigB_Positive, "the measured magnitude is not in doubt, only the difference");
            settings.OAPABacklashPairSchema.Should().Be(2, "the schema-0 reset lands directly on the current schema");

            // Idempotent: a later genuine directional pair must survive re-entry.
            settings.OAPAXBacklashCompensationNegative = 4.01f;
            OapaSettingsMigration.EnsureCurrent();
            settings.OAPAXBacklashCompensationNegative.Should().Be(4.01f);
        }

        [Test]
        public void TheProjectionStep_OnlyMovesTheSchema_AndKeepsEveryStoredValue() {
            // Schema 1 already carries a pair the directionality verdict produced, so there is
            // nothing to undo: the release only changed how a *new* calibration measures. The
            // stored values keep their behaviour until the user re-calibrates, and resetting
            // them here would throw away a genuine directional pair on upgrade.
            var settings = Properties.Settings.Default;
            settings.OAPABacklashPairSchema = 1;
            settings.OAPAXGearRatio = 412f;
            settings.OAPAXBacklashCompensation = 4.01f;
            settings.OAPAXBacklashCompensationNegative = 2.63f;
            settings.OAPAYBacklashCompensationNegative = 1.5f;

            OapaSettingsMigration.EnsureCurrent();

            settings.OAPABacklashPairSchema.Should().Be(2);
            settings.OAPAXBacklashCompensationNegative.Should().Be(2.63f, "a schema-1 pair was gated by the verdict and stays");
            settings.OAPAYBacklashCompensationNegative.Should().Be(1.5f);
            settings.OAPAXBacklashCompensation.Should().Be(4.01f);
            settings.OAPAXGearRatio.Should().Be(412f);
        }

        [Test]
        public void TheMeanIsNotFreeOnAGenuinelyDirectionalAxis_WhichIsWhyTheVerdictGatesIt() {
            // Stated so the trade is explicit rather than implied: collapsing rig C's real
            // 2.63/4.01 to its mean leaves the whole real difference behind on every
            // reversal. That is the cost the directionality verdict is there to avoid
            // paying, and the reason the fix is a gate and not a removal.
            var mean = (2.63f + 4.01f) / 2f;

            Arrive(Plan(-0.23f, mean, mean), realEnteringPositive: 2.63f, realEnteringNegative: 4.01f)
                .Should().BeApproximately(-0.23f - (2.63f - 4.01f), 0.01f);
        }
    }
}
