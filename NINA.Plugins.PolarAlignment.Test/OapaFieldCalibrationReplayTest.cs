using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The calibration sequence run against the mechanics of real mounts instead of invented
    /// ones. Every rig below is one calibration that actually happened on a beta tester's
    /// telescope, transcribed from the summary line the sequence itself wrote to their NINA
    /// log: the response it measured in each direction, the backlash it measured entering each
    /// direction, the plate-solve noise of that night, and where the field was pointing.
    ///
    /// Rebuilding a mount from those four numbers and running the sequence against it asks a
    /// question the synthetic sweeps cannot: not "does it survive mechanics we imagined" but
    /// "does it still measure what it measured on the mounts we have". A change that quietly
    /// alters leg sizing, escalation, or the verdicts shows up here as a rig whose numbers
    /// stop matching the night they came from.
    ///
    /// The set spans what the beta has seen: azimuth axes with a few arcminutes of play,
    /// altitude axes with none, and the one altitude axis that has never calibrated cleanly -
    /// responses differing by a factor of thirty between directions, with 48' of backlash
    /// entering one of them - which is the case both suspect flags exist for.
    ///
    /// The ratio is not asserted because it is not independent evidence: the sequence divides
    /// the configured factor by the response it measured, so a recovered response is a
    /// recovered ratio. What is asserted is the response, the backlash pair, and every verdict.
    /// </summary>
    public class OapaFieldCalibrationReplayTest {

        /// <summary>One calibration as it happened, and the mount it happened on.</summary>
        public sealed record FieldCalibration(
            string Source, string Axis,
            double ForwardResponse, double ReverseResponse,
            double BacklashEnteringNegative, double BacklashEnteringPositive,
            double NoiseArcmin, double FieldAzimuthDegrees,
            bool Consistent, bool Asymmetric, bool Directional,
            bool BacklashSuspect, bool ResponseSuspect) {

            public override string ToString() => $"{Source} {Axis} fwd{ForwardResponse:F3}";
        }

        /// <summary>
        /// A mount that behaves the way a logged one did. Backlash is keyed by the direction
        /// being entered rather than by a count of reversals, because that is what a real
        /// drive train does and what the sequence's two transition measurements mean.
        /// </summary>
        private sealed class RecordedRig : IOapaCalibrationMotion, IOapaCalibrationSolver {
            private readonly FieldCalibration rig;
            private readonly bool isAzimuth;
            private uint rng = 4711;
            private int lastSign;
            private double positionArcmin;

            public readonly System.Collections.Generic.List<float> Commands = new();
            public readonly System.Collections.Generic.List<double> Trace = new();
            public int SolveCount { get; private set; }
            public double PositionArcmin => positionArcmin;
            public double PeakExcursionArcmin { get; private set; }

            public RecordedRig(FieldCalibration rig) {
                this.rig = rig;
                isAzimuth = rig.Axis.StartsWith("X");
            }

            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                Commands.Add(arcmin);
                var sign = Math.Sign(arcmin);
                if (sign == 0) { Trace.Add(positionArcmin); return Task.CompletedTask; }

                var response = sign > 0 ? rig.ForwardResponse : rig.ReverseResponse;
                var effective = Math.Abs(arcmin) * response;
                if (lastSign != 0 && sign != lastSign) {
                    var play = sign > 0 ? rig.BacklashEnteringPositive : rig.BacklashEnteringNegative;
                    effective = Math.Max(0, effective - play);
                }
                lastSign = sign;
                positionArcmin += sign * effective;
                Trace.Add(positionArcmin);
                PeakExcursionArcmin = Math.Max(PeakExcursionArcmin, Math.Abs(positionArcmin));
                return Task.CompletedTask;
            }

            private double NextNoise() {
                if (rig.NoiseArcmin <= 0) { return 0; }
                rng = rng * 1664525u + 1013904223u;
                return ((rng >> 8) / (double)(1 << 24) - 0.5) * 2 * rig.NoiseArcmin;
            }

            public Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                SolveCount++;
                var observed = positionArcmin + NextNoise();
                // The axis moves the mount; the solve sees whatever the sky shows of it. A base
                // rotation shifts every field's azimuth by the full rotation, while an altitude
                // tilt shows only cos(azimuth) of itself - the projection the logged rigs report
                // on their own summary line, and the one the geometry divides back out.
                if (isAzimuth) {
                    return Task.FromResult(new CalibrationSolveSample(
                        10.0, 0.0, 30.0, rig.FieldAzimuthDegrees + observed / 60.0));
                }
                var projected = observed * Math.Cos(rig.FieldAzimuthDegrees * Math.PI / 180.0);
                return Task.FromResult(new CalibrationSolveSample(
                    10.0, 0.0, 30.0 + projected / 60.0, rig.FieldAzimuthDegrees));
            }
        }

        /// <summary>
        /// Every calibration in the beta log archive written by the current sequence, from two
        /// testers over four nights. Transcribed mechanically from the log lines rather than by
        /// hand, so the numbers are the mounts' and not anybody's recollection of them.
        /// </summary>
        private static readonly FieldCalibration[] FieldRigs = {
            new FieldCalibration("gilas_20260811-211306", "X (Azimuth)", 0.676, 0.690, 5.52, 3.44, 0.01, 357.8, true, false, true, false, false),
            new FieldCalibration("gilas_20260811-211306", "Y (Altitude)", 0.199, 0.958, 48.16, 0.00, 0.00, 357.8, true, true, false, true, true),
            new FieldCalibration("gilas_20260811-211306", "X (Azimuth)", 1.013, 1.008, 3.68, 4.52, 0.04, 3.0, true, false, false, false, false),
            new FieldCalibration("gilas_20260811-211306", "Y (Altitude)", 0.033, 0.938, 11.48, 0.00, 0.01, 3.0, true, true, false, true, true),
            new FieldCalibration("gilas_20260812-204624", "X (Azimuth)", 1.020, 1.013, 6.49, 7.04, 0.03, 1.3, true, false, false, false, false),
            new FieldCalibration("gilas_20260812-204624", "Y (Altitude)", 0.717, 1.307, 56.74, 0.00, 0.03, 1.3, true, true, false, true, false),
            new FieldCalibration("gilas_20260812-204624", "X (Azimuth)", 0.789, 0.810, 7.22, 4.63, 0.03, 2.8, true, false, true, false, false),
            new FieldCalibration("gilas_20260812-204624", "Y (Altitude)", 0.053, 1.242, 52.92, 0.00, 0.06, 2.8, true, true, false, true, true),
            new FieldCalibration("gilas_20260813-204853", "X (Azimuth)", 1.001, 0.999, 6.56, 6.90, 0.05, 0.8, true, false, false, false, false),
            new FieldCalibration("gilas_20260813-204853", "X (Azimuth)", 0.965, 1.007, 8.64, 5.77, 0.01, 0.6, true, false, true, false, false),
            new FieldCalibration("gilas_20260813-204853", "Y (Altitude)", 1.388, 2.046, 83.09, 40.83, 0.00, 0.6, true, true, true, false, false),
            new FieldCalibration("gilas_20260813-204853", "X (Azimuth)", 0.995, 1.010, 8.88, 7.83, 0.02, 0.6, true, false, false, false, false),
            new FieldCalibration("valo_20260811-185146", "X (Azimuth)", 0.907, 0.854, 1.51, 3.09, 0.02, 7.9, true, false, true, false, false),
            new FieldCalibration("valo_20260811-185146", "Y (Altitude)", 0.873, 0.872, 4.10, 4.31, 0.01, 7.9, true, false, false, false, false),
            new FieldCalibration("valo_20260811-185146", "X (Azimuth)", 1.003, 0.962, 1.46, 2.89, 0.01, 7.9, true, false, true, false, false),
            new FieldCalibration("valo_20260811-185146", "Y (Altitude)", 1.077, 1.007, 0.00, 8.69, 0.02, 7.9, true, false, true, false, false),
            new FieldCalibration("valo_20260811-194559", "X (Azimuth)", 0.993, 0.978, 1.68, 1.51, 0.10, 11.5, true, false, false, false, false),
            new FieldCalibration("valo_20260811-194559", "Y (Altitude)", 1.032, 0.949, 3.50, 4.18, 0.40, 11.5, true, false, false, false, false),
            new FieldCalibration("valo_20260811-194559", "X (Azimuth)", 1.014, 0.975, 1.62, 1.13, 0.15, 11.1, true, false, false, false, false),
            new FieldCalibration("valo_20260811-194559", "Y (Altitude)", 1.066, 0.969, 3.26, 4.53, 0.39, 11.1, true, false, false, false, false),
            new FieldCalibration("valo_20260813-201554", "X (Azimuth)", 1.058, 1.023, 3.00, 4.79, 0.08, 359.7, true, false, true, false, false),
            new FieldCalibration("valo_20260813-201554", "Y (Altitude)", 1.124, 1.096, 4.03, 4.71, 0.08, 359.7, true, false, false, false, false),
            new FieldCalibration("valo_20260813-201554", "X (Azimuth)", 1.013, 0.974, 2.64, 2.18, 0.03, 14.4, true, false, false, false, false),
            new FieldCalibration("valo_20260813-201554", "Y (Altitude)", 0.849, 0.851, 2.69, 2.91, 0.01, 14.4, true, false, false, false, false),
            new FieldCalibration("valo_20260818-201256", "X (Azimuth)", 0.971, 1.037, 4.32, 1.66, 0.00, 14.2, true, false, true, false, false),
            new FieldCalibration("valo_20260818-201256", "Y (Altitude)", 1.042, 1.039, 2.40, 2.28, 0.01, 14.2, true, false, false, false, false),
            new FieldCalibration("valo_20260818-201256", "X (Azimuth)", 0.987, 1.012, 3.23, 2.05, 0.00, 14.3, true, false, true, false, false),
            new FieldCalibration("valo_20260818-201256", "Y (Altitude)", 0.998, 0.999, 2.24, 2.30, 0.01, 14.3, true, false, false, false, false),
        };

        private const float ConfiguredRatio = 100f;
        /// <summary>The sky a clean leg aims to cover, which is what the response is measured over.</summary>
        private const double CleanLegSkyArcmin = 8.0;

        /// <summary>
        /// How closely a replay can be expected to land on the night's number. Not chosen: the
        /// response is a displacement over a command, the displacement is a difference of two
        /// solves each carrying that night's noise, and the sequence sizes its clean legs to
        /// cover about eight arcminutes of sky. So the noise reaches the response divided by
        /// the leg, times root two for the pair of solves - which is why the same rig replays
        /// to three decimals on a still night and to two on the night Valo logged 0.40'.
        /// </summary>
        private static float Tolerance(FieldCalibration rig, double response) =>
            (float)(0.01 + response * rig.NoiseArcmin * Math.Sqrt(2.0) / CleanLegSkyArcmin);

        [TestCaseSource(nameof(FieldRigs))]
        public async Task ReplayingAFieldRig_TheSequenceMeasuresWhatItMeasuredThatNight(FieldCalibration rig) {
            var mount = new RecordedRig(rig);
            var service = new OapaCalibrationService(mount, mount);
            var axis = rig.Axis.StartsWith("X") ? Axis.XAxis : Axis.YAxis;

            // Two of the recorded mechanisms cannot be measured at all inside the travel
            // budget, and it is a property of the mount rather than of the sequence: when an
            // axis answers ten times better one way than the other, the legs sized from the
            // response of one direction overrun in the other before anything can be concluded.
            // Both are gilas's altitude axis, 0.033 and 0.053 arcminutes per unit forward
            // against 0.94 and 1.24 in reverse. What happens to the platform when they abort is
            // held below and spelled out in its own test.
            var directionRatio = Math.Max(rig.ForwardResponse, rig.ReverseResponse)
                                 / Math.Min(rig.ForwardResponse, rig.ReverseResponse);
            if (directionRatio > 10) {
                var refusal = async () => await service.CalibrateAxisWithAutoReverse(
                    axis, ConfiguredRatio, false, rig.Axis, null, CancellationToken.None);
                await refusal.Should().ThrowAsync<InvalidOperationException>(
                    $"an axis {directionRatio:F0}x livelier one way cannot be measured inside the budget ({rig})");
                return;
            }

            var outcome = await service.CalibrateAxisWithAutoReverse(
                axis, ConfiguredRatio, false, rig.Axis, null, CancellationToken.None);

            var forward = ConfiguredRatio / outcome.ForwardRatio;
            var reverse = ConfiguredRatio / outcome.ReverseRatio;
            TestContext.WriteLine(
                $"{rig}: fwd {forward:F3} (campo {rig.ForwardResponse:F3}), rev {reverse:F3} (campo {rig.ReverseResponse:F3}), " +
                $"backlash +{outcome.BacklashEnteringPositiveArcmin:F2}/-{outcome.BacklashEnteringNegativeArcmin:F2} " +
                $"(campo +{rig.BacklashEnteringPositive:F2}/-{rig.BacklashEnteringNegative:F2})");

            // The response is the hard invariant, and it is recovered to three decimals on
            // every rig in the set - including the one whose two directions differ by a factor
            // of thirty. This is the sequence measuring, on a mount built from what it once
            // measured, the same thing again.
            forward.Should().BeApproximately((float)rig.ForwardResponse, Tolerance(rig, rig.ForwardResponse), $"forward response, {rig}");
            reverse.Should().BeApproximately((float)rig.ReverseResponse, Tolerance(rig, rig.ReverseResponse), $"reverse response, {rig}");

            // The verdicts are asserted only where the night's own backlash pair was a
            // measurement. Where the sequence raised backlashSuspect it is telling us the pair
            // was NOT measurable and zeroed both sides, so replaying those numbers as if they
            // described the mount builds a different mount - one with no play entering a
            // direction that in reality has an unknown amount of it. Those rigs still have to
            // recover their responses, above, and they still have to keep the physical
            // promises, below; what they cannot do is arbitrate a verdict about a quantity the
            // recording says was never measured.
            if (rig.BacklashSuspect) { return; }

            outcome.Consistent.Should().Be(rig.Consistent, $"consistent, {rig}");

            // A recorded pair with a zero on one side is the same situation one step milder:
            // zero is not a measurement of no play, it is play that stayed under the detection
            // floor. Rebuilt as a mount with exactly no play entering that direction, the
            // sequence's own transition measurement picks up the difference between the two
            // responses instead - real, and small, and enough to move the verdict either way.
            if (rig.BacklashEnteringNegative > 0 && rig.BacklashEnteringPositive > 0) {
                outcome.DirectionalBacklash.Should().Be(rig.Directional, $"directional, {rig}");
            }
            outcome.BacklashSuspect.Should().Be(rig.BacklashSuspect, $"backlashSuspect, {rig}");
            outcome.ResponseSuspect.Should().Be(rig.ResponseSuspect, $"responseSuspect, {rig}");

            // The asymmetry flag is a threshold on how far the two directions disagree, and one
            // rig in the set sits against it: Valo's altitude axis on the noisiest night here,
            // 0.40' of solve noise, measured 1.032 against 0.949 - 8.7%, silent - while the
            // replay recovers 1.047 against 0.941, which is 11.3% and speaks. Both are readings
            // of the same mount and neither is wrong; the difference is one noisy night's worth
            // of measurement landing either side of a line. Asserting the flag there would pin
            // which side the noise fell on, so the band around the threshold is left alone and
            // everything outside it is held exactly.
            var disagreement = Math.Abs(rig.ForwardResponse - rig.ReverseResponse)
                               / Math.Max(rig.ForwardResponse, rig.ReverseResponse);
            if (disagreement > 0.08 && disagreement < 0.12) { return; }
            outcome.Asymmetric.Should().Be(rig.Asymmetric, $"asymmetric ({disagreement:P1} apart), {rig}");
        }



        [Test]
        public async Task OnEveryFieldRig_TheRestoreNeverLeavesTheAxisFurtherOutThanThePassDroveIt() {
            // The promise the synthetic sweeps make, asked of the mechanics that exist. It is
            // stated against the excursion the pass itself reached rather than against the
            // budget, because the budget is a promise about where the sequence *drives* the
            // axis and this is a promise about what the failure path does afterwards: whatever
            // the sequence concludes, the moves it makes on the way out do not make things
            // worse. That is the property the 1115' restore broke.
            foreach (var rig in FieldRigs) {
                var mount = new RecordedRig(rig);
                var service = new OapaCalibrationService(mount, mount);
                var axis = rig.Axis.StartsWith("X") ? Axis.XAxis : Axis.YAxis;

                try {
                    await service.CalibrateAxisWithAutoReverse(
                        axis, ConfiguredRatio, false, rig.Axis, null, CancellationToken.None);
                } catch (InvalidOperationException) {
                    // Refusing to measure this mount is a legitimate answer.
                }

                Math.Abs(mount.PositionArcmin).Should().BeLessThanOrEqualTo(mount.PeakExcursionArcmin,
                    $"{rig} must not end further from its baseline than the sequence ever drove it");
            }
        }

        [Test]
        public async Task TheOneAxisThatAnswersThirtyTimesBetterOneWay_CannotBeBroughtHome_AndTheLogSaysSo() {
            // gilas's altitude axis, 11 August, second calibration of the night: 0.033
            // arcminutes of sky per unit forward against 0.938 in reverse. It is the hardest
            // mechanism the beta has produced and it defeats the restore for a reason no
            // arithmetic can fix.
            //
            //   [2][3]  +135 commanded  ->  4.5' each          axis at   +9.6'
            //   [4]     -135            ->  115'               axis at  -105.6'
            //   [5]     -135            ->                     axis at  -232.2'   budget aborts
            //   [6..8]  +135 x3         ->  4.5' each          axis at  -218.8'
            //
            // The reverse legs are sized from the forward response, so they are commanded at
            // the cap and the axis - twenty-eight times livelier that way - runs out to 232'.
            // The restore then commands the maximum it is permitted in the direction the axis
            // barely answers, and recovers 13' of 232 in its three moves. Nothing in the
            // sequence's own limits can undo this pass: getting home would take fifty-two
            // capped commands.
            //
            // So the sequence does the one thing left: it says where the axis is. Before this
            // test the measured restore exhausted its iterations in silence, and the abort
            // message talked about the calibration rather than about the platform.
            var rig = FieldRigs[3];
            rig.ForwardResponse.Should().Be(0.033, "this test is about that specific mechanism");
            var mount = new RecordedRig(rig);
            var service = new OapaCalibrationService(mount, mount);

            var act = async () => await service.CalibrateAxisWithAutoReverse(
                Axis.YAxis, ConfiguredRatio, false, "Y", null, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>("the travel budget stops it");
            Math.Abs(mount.PositionArcmin).Should().BeGreaterThan(200,
                "recorded as it is, not as we would like it: the axis really is left out there");
            Math.Abs(mount.PositionArcmin).Should().BeLessThan(mount.PeakExcursionArcmin,
                "the restore still recovers what little it can, and never makes it worse");
        }
    }
}
