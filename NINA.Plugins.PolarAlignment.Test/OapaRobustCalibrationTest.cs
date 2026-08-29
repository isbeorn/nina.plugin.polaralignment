using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Exercises the staged self-scaling calibration (noise floor, probe escalation, clean
    /// forward/reverse legs, backlash-leg escalation, slippage detection) against a richer
    /// axis simulator: direction-dependent response, per-reversal backlash variation and
    /// deterministic seeded solve noise.
    /// </summary>
    public class OapaRobustCalibrationTest {

        /// <summary>
        /// Physical axis simulator. Response scale may differ per direction (mechanical
        /// asymmetry); backlash values are consumed per direction reversal from a cycling
        /// sequence (a varying sequence models slippage, a single value repeatable
        /// mechanics); solve noise is deterministic from the seed.
        /// </summary>
        private sealed class RobustFakeAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly double forwardScale;
            private readonly double reverseScale;
            private readonly double[] backlashSequence;
            private readonly double noiseAmplitudeArcmin;
            /// <summary>-1 models a rig wired so the sky moves opposite the commanded sign: the case the Reverse flag exists for.</summary>
            private readonly int physicalSign;
            /// <summary>
            /// Where the camera points while calibrating. The altitude actuator tilts the rig
            /// about the horizontal east-west axis, so the field's altitude only shows
            /// cos(azimuth) of the physical tilt: full toward north (the default), reversed
            /// toward south, nothing due east/west. The fake models that projection so the
            /// service is proven to undo it.
            /// </summary>
            private readonly double fieldAzimuthDegrees;
            private uint rng;
            private int reversals;
            private double physicalPositionArcmin;
            private int lastSign;

            public int SolveCount { get; private set; }
            public readonly List<float> CommandedMoves = new();

            public RobustFakeAxis(double forwardScale,
                                  double? reverseScale = null,
                                  double[] backlashSequence = null,
                                  double noiseAmplitudeArcmin = 0,
                                  int seed = 12345,
                                  int physicalSign = 1,
                                  double fieldAzimuthDegrees = 0.0) {
                this.forwardScale = forwardScale;
                this.reverseScale = reverseScale ?? forwardScale;
                this.backlashSequence = backlashSequence ?? new[] { 0.0 };
                this.noiseAmplitudeArcmin = noiseAmplitudeArcmin;
                this.physicalSign = physicalSign;
                this.fieldAzimuthDegrees = fieldAzimuthDegrees;
                rng = (uint)seed;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                CommandedMoves.Add(arcmin);
                var sign = Math.Sign(arcmin);
                var scale = sign >= 0 ? forwardScale : reverseScale;
                double effective = Math.Abs(arcmin) * scale;
                if (sign != 0 && lastSign != 0 && sign != lastSign) {
                    var backlash = backlashSequence[reversals % backlashSequence.Length];
                    reversals++;
                    effective = Math.Max(0, effective - backlash);
                }
                if (sign != 0) { lastSign = sign; }
                physicalPositionArcmin += physicalSign * sign * effective;
                return Task.CompletedTask;
            }

            private double NextNoise() {
                if (noiseAmplitudeArcmin <= 0) { return 0; }
                rng = rng * 1664525u + 1013904223u;
                return ((rng >> 8) / (double)(1 << 24) - 0.5) * 2 * noiseAmplitudeArcmin;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                SolveCount++;
                var projection = Math.Cos(fieldAzimuthDegrees * Math.PI / 180.0);
                var observed = physicalPositionArcmin * projection + NextNoise();
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, observed / 60.0, 30.0 + observed / 60.0, fieldAzimuthDegrees));
            }

            public double PhysicalPositionArcmin => physicalPositionArcmin;
        }

        private static Task<AxisCalibrationOutcome> Calibrate(RobustFakeAxis axis, float currentRatio = 100f, bool reversed = false) {
            var service = new OapaCalibrationService(axis, axis);
            return service.CalibrateAxisWithAutoReverse(Axis.YAxis, currentRatio, reversed, "Y", null, CancellationToken.None);
        }

        [Test]
        public async Task GilasCase_BacklashLargerThanInitialLeg_RecoversRatioAndPhysicalBacklash() {
            // The field failure that motivated the staged sequence: the mechanics lose 20'
            // per reversal while the initial legs are ~8' physical. Fixed-leg measurement
            // is fully contaminated; the escalating backlash leg must grow until the
            // shortfall is a minority share, recovering both values.
            var axis = new RobustFakeAxis(forwardScale: 0.25, backlashSequence: new[] { 20.0 });

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeApproximately(400f, 20f, "a quarter of the motion means four times the factor");
            outcome.BacklashArcmin.Should().BeApproximately(20f, 2f, "backlash is physical and must not be contaminated by short legs");
            outcome.DirectionalBacklash.Should().BeFalse("a play that costs the same both ways is not directional");
        }

        [Test]
        public async Task GilasCase_ReturnsAxisNearItsStart() {
            var axis = new RobustFakeAxis(forwardScale: 0.25, backlashSequence: new[] { 20.0 });

            await Calibrate(axis);

            axis.PhysicalPositionArcmin.Should().BeApproximately(0.0, 1.0);
        }

        [Test]
        public async Task NearlyDeafAxis_EscalatesTheProbeUntilMotionIsVisible() {
            // 2% response: the initial 5' probe moves 0.1', below the detection floor.
            // The probe must escalate until the motion is measurable, then calibrate.
            var axis = new RobustFakeAxis(forwardScale: 0.02);

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeApproximately(5000f, 250f);
        }

        [Test]
        public async Task DeadAxis_FailsHonestlyAfterEscalation() {
            var axis = new RobustFakeAxis(forwardScale: 0.0001);

            var act = () => Calibrate(axis);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*did not move measurably*");
        }

        [Test]
        public async Task NoisySolves_StillRecoverRatioAndBacklash() {
            var axis = new RobustFakeAxis(forwardScale: 0.5, backlashSequence: new[] { 5.0 }, noiseAmplitudeArcmin: 0.05);

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeApproximately(200f, 10f);
            outcome.BacklashArcmin.Should().BeApproximately(5f, 1f);
            outcome.NoiseSigmaArcmin.Should().BeGreaterThan(0f);
        }

        [Test]
        public async Task AsymmetricResponse_FlagsAndReportsBothRatios_WithoutFakeBacklash() {
            // 15% direction asymmetry, zero backlash: the flag must fire with both ratios
            // reported, the single model ratio is the mean, and the asymmetry must not
            // leak into the backlash or trip the directionality verdict.
            var axis = new RobustFakeAxis(forwardScale: 1.0, reverseScale: 0.85);

            var outcome = await Calibrate(axis);

            outcome.Asymmetric.Should().BeTrue();
            outcome.ForwardRatio.Should().BeApproximately(100f, 5f);
            outcome.ReverseRatio.Should().BeApproximately(117.6f, 6f);
            outcome.Ratio.Should().BeApproximately(108.1f, 5f);
            outcome.BacklashArcmin.Should().BeLessThan(1f);
            outcome.DirectionalBacklash.Should().BeFalse("a response asymmetry is not a backlash asymmetry");
        }

        [Test]
        public async Task SymmetricResponse_DoesNotFlagAsymmetry() {
            var axis = new RobustFakeAxis(forwardScale: 0.5, backlashSequence: new[] { 5.0 });

            var outcome = await Calibrate(axis);

            outcome.Asymmetric.Should().BeFalse();
        }

        [Test]
        public async Task TransitionsThatCostDifferently_AreFlaggedAsDirectional_AndBothAreReported() {
            // An axis loaded by gravity crosses its own play unaided one way and has to be
            // driven across it the other, so the two transitions legitimately differ. The
            // mean is inexact for both directions, so the caller needs the two figures, not
            // only the flag.
            var axis = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 20.0, 8.0 });

            var outcome = await Calibrate(axis);

            outcome.DirectionalBacklash.Should().BeTrue();
            outcome.BacklashEnteringNegativeArcmin.Should().BeApproximately(20f, 2f, "the first reversal the sequence meets goes negative");
            outcome.BacklashEnteringPositiveArcmin.Should().BeApproximately(8f, 2f);
            outcome.BacklashArcmin.Should().BeApproximately(14f, 2f, "the applied value is still their mean");
        }

        [Test]
        public async Task PerDirectionBacklash_IsReportedInCommandedSign_NotInTheCalibrationsOwnLegs() {
            // The calibration's "forward" leg is the commanded positive direction only when
            // Reverse is off; with Reverse on it is the commanded negative one. The same
            // mechanism must therefore report its two figures the other way round, because
            // the planner works in commanded sign and knows nothing about Reverse.
            //
            // Same 20'/8' mechanism, wired so the sky moves against the commanded sign, and
            // calibrated with Reverse already on so no auto-flip intervenes.
            var straight = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 20.0, 8.0 });
            var mirrored = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 20.0, 8.0 }, physicalSign: -1);

            var normal = await Calibrate(straight);
            var reversed = await Calibrate(mirrored, reversed: true);

            normal.Consistent.Should().BeTrue();
            reversed.Consistent.Should().BeTrue();
            reversed.Flipped.Should().BeFalse("Reverse was already correct, so nothing had to be flipped");

            normal.BacklashEnteringNegativeArcmin.Should().BeApproximately(20f, 2f);
            normal.BacklashEnteringPositiveArcmin.Should().BeApproximately(8f, 2f);

            reversed.BacklashEnteringPositiveArcmin.Should().BeApproximately(20f, 2f,
                "with Reverse on, the calibration's first reversal travels the commanded positive direction");
            reversed.BacklashEnteringNegativeArcmin.Should().BeApproximately(8f, 2f);
        }

        [Test]
        public async Task TransitionsThatCostTheSame_AreNotDirectional() {
            var axis = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 12.0 });

            var outcome = await Calibrate(axis);

            outcome.DirectionalBacklash.Should().BeFalse();
            outcome.BacklashArcmin.Should().BeApproximately(12f, 1f);
            outcome.BacklashEnteringNegativeArcmin.Should().BeApproximately(
                outcome.BacklashEnteringPositiveArcmin, 2f, "a symmetric mechanism reports the same both ways");
        }

        // ----- What the pair is allowed to say -----
        // A two-leg reversal plan travels `move - outward + back`, so any gap between the
        // reported pair is a fixed bias on every reversal that only real play cancels. The
        // pair may therefore be split only when the difference has been established, and
        // must not be reported at all when the pass did not measure one.

        [Test]
        public async Task NotDirectional_ReportsTheTwoDirectionsIdentical_NotTwoNoisySamples() {
            // A mechanism whose two transitions differ by less than the verdict's threshold.
            // Reporting 11.6 and 12.4 separately would hand the planner a 0.8' floor made of
            // measurement noise; reporting the mean twice hands it nothing.
            var axis = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 12.0, 11.0 });

            var outcome = await Calibrate(axis);

            outcome.DirectionalBacklash.Should().BeFalse();
            outcome.BacklashEnteringPositiveArcmin.Should().Be(outcome.BacklashEnteringNegativeArcmin,
                "an unestablished difference must be exactly zero, not small");
            outcome.BacklashEnteringPositiveArcmin.Should().Be(outcome.BacklashArcmin);
        }

        /// <summary>
        /// Mechanism for the travel-budget tests: responds with a fixed scale and,
        /// optionally, creeps a fixed amount further on every move (a slipping clutch, a
        /// platform settling under load) - motion the commanded sum knows nothing about.
        /// </summary>
        private sealed class RunawayFakeAxis : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly double scale;
            private readonly double creepPerMoveArcmin;
            private double physical;
            public float LargestCommandArcmin { get; private set; }

            public RunawayFakeAxis(double scale, double creepPerMoveArcmin = 0) {
                this.scale = scale;
                this.creepPerMoveArcmin = creepPerMoveArcmin;
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                LargestCommandArcmin = Math.Max(LargestCommandArcmin, Math.Abs(arcmin));
                physical += arcmin * scale + creepPerMoveArcmin;
                return Task.CompletedTask;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                return Task.FromResult(new CalibrationSolveSample(10.0, physical / 60.0, 30.0 + physical / 60.0, 0.0));
            }
        }

        [Test]
        public async Task AnAxisThatKeepsCreepingAway_IsStoppedAtTheTravelBudget() {
            // Every move drags the platform 60' further than commanded. The commanded sum
            // stays small, so only a budget measured on the sky can see the runaway: the
            // calibration must abort before the platform is driven into its travel limit.
            var axis = new RunawayFakeAxis(scale: 1.0, creepPerMoveArcmin: 60.0);
            var service = new OapaCalibrationService(axis, axis);

            var act = () => service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*travel budget*", "the abort must say why, in terms of protecting the axis travel");
        }

        [Test]
        public async Task AFaintResponse_NeverProducesAMonsterLeg() {
            // The legs self-scale to the measured response, so a 0.04 response would ask
            // for a 200' leg. One command that large is not a measurement any more - it
            // is three degrees of travel bet on a single number. The cap bounds every
            // commanded move at the same three-steps limit the closing moves use.
            var axis = new RunawayFakeAxis(scale: 0.04);
            var service = new OapaCalibrationService(axis, axis);

            var outcome = await service.CalibrateAxisWithAutoReverse(Axis.YAxis, 100f, false, "Y", null, CancellationToken.None);

            axis.LargestCommandArcmin.Should().BeLessThanOrEqualTo(135f);
            outcome.Ratio.Should().BeApproximately(2500f, 250f, "the factor is still recovered from the capped legs");
        }

        [Test]
        public async Task GilasCase_SuspectResponses_TakeTheFactorFromTheStrongDirection() {
            // Field case: an axis losing steps against gravity responded 0.199 forward
            // and 0.958 in reverse. A factor error is a scale - it affects both
            // directions identically - so a 5x disagreement means the weak direction is
            // measuring a malfunction, not the scale. The old mean produced a factor
            // 1.7x too large, and recalibrating on top of it compounded to 3.6x.
            var axis = new RobustFakeAxis(forwardScale: 0.2, reverseScale: 0.96);

            var outcome = await Calibrate(axis);

            outcome.ResponseSuspect.Should().BeTrue();
            outcome.Ratio.Should().BeApproximately(100f / 0.96f, 6f,
                "the strong direction is within a few percent of the true scale; the mean would be off by ~70%");
        }

        [Test]
        public async Task ValoCase_ZeroAgainstLarge_IsCollapsedToTheMean_NotSplit() {
            // Field case, twice on the same rig: an axis that measured 4.10'/4.31'
            // (symmetric) reported 0.00'/8.69' five minutes later - stable sum, flipped
            // split. Zero-against-large is the signature of a transition that slipped
            // during measurement, not of directional mechanics, and the phantom split
            // threw a 23" residual to 6'32" right at the finish line. A pair with one
            // side indistinguishable from zero must not establish directionality; the
            // mean is the safe collapse because a symmetric value's magnitude cancels
            // out of the two-leg plan.
            var axis = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 8.7, 0.0 });

            var outcome = await Calibrate(axis);

            outcome.DirectionalBacklash.Should().BeFalse(
                "one transition indistinguishable from zero cannot establish a directional split");
            outcome.BacklashEnteringPositiveArcmin.Should().Be(outcome.BacklashEnteringNegativeArcmin,
                "the pair must collapse to a single symmetric value");
            outcome.BacklashArcmin.Should().BeApproximately(4.35f, 1f,
                "the mean keeps the real (symmetric) play compensated instead of discarding it");
            outcome.BacklashSuspect.Should().BeFalse(
                "the mean is usable - this is a collapsed split, not an unmeasurable pair");
        }

        [Test]
        public async Task Directional_KeepsTheTwoDirectionsApart() {
            var axis = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { 20.0, 8.0 });

            var outcome = await Calibrate(axis);

            outcome.DirectionalBacklash.Should().BeTrue();
            outcome.BacklashEnteringNegativeArcmin.Should().BeGreaterThan(
                outcome.BacklashEnteringPositiveArcmin + 5f, "the established difference survives");
        }

        [Test]
        public async Task ImpossibleTransition_IsZeroedOnBothDirections_NotPairedAgainstTheOther() {
            // A reversal that travels *further* than the response predicts - a preload
            // letting go, a stick-slip release - produces a negative transition that the
            // clamp turns into a plausible "no play this way". Paired against a real 8' in
            // the other direction that zero would become 8' of compensation made of nothing.
            // Field precedent: a calibration reported 0.00'/27.21' and offered it.
            var axis = new RobustFakeAxis(forwardScale: 1.0, backlashSequence: new[] { -12.0, 8.0 });

            var outcome = await Calibrate(axis);

            outcome.BacklashSuspect.Should().BeTrue();
            outcome.BacklashArcmin.Should().Be(0f);
            outcome.BacklashEnteringPositiveArcmin.Should().Be(0f);
            outcome.BacklashEnteringNegativeArcmin.Should().Be(0f);
            outcome.DirectionalBacklash.Should().BeFalse();
        }

        [Test]
        public async Task ResponsesDisagreeingByMoreThanTwice_AreFlagged_AndTakeTheBacklashWithThem() {
            // Field case: fwd=0.860 against rev=0.102 on an altitude axis. The mean of those
            // is wrong for both directions, and each backlash transition is evaluated against
            // the *other* direction's response - so nothing this pass measured is usable.
            var axis = new RobustFakeAxis(forwardScale: 1.0, reverseScale: 0.12);

            var outcome = await Calibrate(axis);

            outcome.ResponseSuspect.Should().BeTrue();
            outcome.Asymmetric.Should().BeTrue("the softer flag fires too");
            outcome.BacklashSuspect.Should().BeTrue();
            outcome.BacklashEnteringPositiveArcmin.Should().Be(0f);
            outcome.BacklashEnteringNegativeArcmin.Should().Be(0f);
        }

        [Test]
        public async Task ModerateResponseAsymmetry_IsFlaggedButStillUsable() {
            // 15% is a real axis with a heavier direction, not a broken measurement: the
            // stronger flag must not fire and the backlash must survive.
            var axis = new RobustFakeAxis(forwardScale: 1.0, reverseScale: 0.85, backlashSequence: new[] { 6.0 });

            var outcome = await Calibrate(axis);

            outcome.Asymmetric.Should().BeTrue();
            outcome.ResponseSuspect.Should().BeFalse();
            outcome.BacklashSuspect.Should().BeFalse();
            outcome.BacklashArcmin.Should().BeApproximately(6f, 1.5f);
        }

        // The two capstone tests that drive the reported pair through the backlash-mode
        // planner against the same mechanism live in the beta suite and join this file
        // with the backlash-modes PR of the series.

        [Test]
        public async Task SolveBudget_IsRespected_NominalAndGilas() {
            var nominal = new RobustFakeAxis(forwardScale: 0.5, backlashSequence: new[] { 5.0 });
            await Calibrate(nominal);
            nominal.SolveCount.Should().BeLessThanOrEqualTo(15);

            var gilas = new RobustFakeAxis(forwardScale: 0.25, backlashSequence: new[] { 20.0 });
            await Calibrate(gilas);
            gilas.SolveCount.Should().BeLessThanOrEqualTo(20);
        }

        [Test]
        public async Task BacklashStaysPhysical_AcrossRatioErrorsAndSizes() {
            // Invariant ported from the fixed-leg suite: whatever the ratio error, the
            // recovered backlash is the physical loss per reversal.
            foreach (var (scale, backlash) in new[] { (1.0, 0.0), (1.0, 2.5), (0.5, 10.0), (2.0, 2.5) }) {
                var axis = new RobustFakeAxis(forwardScale: scale, backlashSequence: new[] { backlash });

                var outcome = await Calibrate(axis);

                outcome.Ratio.Should().BeApproximately((float)(100.0 / scale), (float)(5.0 / scale),
                    $"scale={scale}, backlash={backlash}");
                outcome.BacklashArcmin.Should().BeApproximately((float)backlash, 0.5f,
                    $"scale={scale}, backlash={backlash}");
            }
        }

        [Test]
        public async Task AyhanCase_CalibratedFarFromTheMeridian_StillRecoversTheTrueFactor() {
            // Field case: a healthy axis calibrated pointing at azimuth 137° reported a
            // factor inflated by exactly 1/|cos(137°)| — 202.7 steps/arcmin for a mechanism
            // that delivered 85 in the very same log. The calibration must divide the
            // measured altitude displacement by the signed projection and recover the true
            // factor from any admitted pointing. (The same rig's az-108 session sits below
            // the projection floor and is refused outright — see the refusal test.)
            var axis = new RobustFakeAxis(forwardScale: 1.0, fieldAzimuthDegrees: 137.2);

            var outcome = await Calibrate(axis);

            outcome.Ratio.Should().BeApproximately(100f, 6f,
                "the factor must describe the axis, not the pointing it was measured through");
            outcome.Flipped.Should().BeFalse(
                "south-of-east pointing flips the raw altitude response; the signed projection must absorb that, not the Reverse flag");
        }

        [Test]
        public async Task TheFactor_IsTheSame_WhereverTheCalibrationPoints() {
            // The rc16 invariant, stated as an invariance: one mechanism, four pointings,
            // one factor. (137° is the field case; 180° the anti-meridian with its sign
            // flip; 0° the pole where nothing changes; 150° a comfortable south-east.)
            foreach (var az in new[] { 0.0, 137.2, 180.0, 150.0 }) {
                var axis = new RobustFakeAxis(forwardScale: 0.5, backlashSequence: new[] { 5.0 }, fieldAzimuthDegrees: az);

                var outcome = await Calibrate(axis);

                outcome.Ratio.Should().BeApproximately(200f, 12f, $"pointing az={az}");
                outcome.BacklashArcmin.Should().BeApproximately(5f, 1f,
                    $"the physical play must not scale with the pointing either (az={az})");
                outcome.Flipped.Should().BeFalse($"the Reverse flag must not depend on the pointing (az={az})");
            }
        }

        [Test]
        public async Task PointingNearDueEastWest_IsRefusedBeforeAnyMotion() {
            // Below the projection floor there is no signal to calibrate on. The refusal
            // must come from the baseline solve, before the axis has been commanded once.
            var axis = new RobustFakeAxis(forwardScale: 1.0, fieldAzimuthDegrees: 78.0);

            var act = () => Calibrate(axis);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*east/west*");
            axis.CommandedMoves.Should().BeEmpty("no motion may be commanded on an unusable pointing");
        }
    }
}
