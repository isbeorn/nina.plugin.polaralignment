using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Direct tests of the pure measurements-to-verdicts derivation. Every rule here was
    /// bought with a field failure; each test carries the numbers of the failure that
    /// motivated it, so a regression reads as "this rig would break again".
    /// </summary>
    public class OapaCalibrationVerdictsTest {

        private static AxisCalibrationMeasurements Healthy() => new(
            CurrentRatio: 100f,
            DirSign: +1f,
            NoiseSigmaArcmin: 0.1,
            DetectionThresholdArcmin: 0.5,
            DirectionConsistent: true,
            ForwardResponse: 1.0,
            ReverseResponse: 1.0,
            BacklashLegArcmin: 45f,
            ReversalTravelArcmin: 40.0,   // 5' lost entering the reversal
            OppositeTravelArcmin: 40.0);  // 5' lost entering the opposite direction

        [Test]
        public void ImpossibleTransition_PairedWithAZeroOther_IsStillSuspect_NotJustNoise() {
            // Review case (upstream #20): an impossible transition whose partner clamps to
            // zero leaves `significant` at zero, so the pair looks like "both below the
            // noise floor" while one of them is physically impossible. The reversal here
            // travelled 10' where the response predicts 5' - twice the predicted travel,
            // which no amount of play can produce - and the pass must say so rather than
            // report a clean "no measurable backlash".
            var m = Healthy() with {
                ForwardResponse = 0.5,
                ReverseResponse = 0.5,
                BacklashLegArcmin = 10f,
                DetectionThresholdArcmin = 1.0,
                ReversalTravelArcmin = 10.0,  // raw forward = 10*0.5 - 10 = -5'
                OppositeTravelArcmin = 5.0    // raw reverse = 10*0.5 - 5  =  0'
            };

            var d = OapaCalibrationVerdicts.Derive(m, "test");

            d.Result.BacklashSuspect.Should().BeTrue(
                "a transition that travelled further than the response predicts invalidates the pair, " +
                "whatever the clamped values look like");
            d.Result.BacklashArcmin.Should().Be(0);
            d.Result.DirectionalBacklash.Should().BeFalse();
        }

        [Test]
        public void ADirectionThatStalled_ReportsNaN_WhileTheScaleComesFromTheHealthyOne() {
            // Review case (upstream #20): dividing by a response of zero yields Infinity,
            // and an infinite "steps per arcminute" is one careless consumer away from the
            // settings. A stalled direction is not a stalled axis though: the healthy
            // direction still measured the scale (the field rule for an axis losing steps
            // against gravity), so the pass keeps its factor and only the stalled
            // direction's own figure reads as nothing.
            var m = Healthy() with { ForwardResponse = 0.0, ReverseResponse = 0.8 };

            var d = OapaCalibrationVerdicts.Derive(m, "test");

            float.IsNaN(d.Result.ForwardRatio).Should().BeTrue("a direction that did not move measured no factor");
            d.Result.Ratio.Should().BeApproximately(100f / 0.8f, 0.01f, "the healthy direction carries the scale");
            float.IsFinite(d.Result.Ratio).Should().BeTrue();
            d.Result.ResponseSuspect.Should().BeTrue("a direction that did not move cannot be half of a usable measurement");
            d.Result.BacklashSuspect.Should().BeTrue();
        }

        [Test]
        public void AnAxisThatMeasuredNothingInEitherDirection_FailsThePass() {
            // No scale was measured at all, so there is no factor to report - only a failed
            // calibration, the same verdict the engagement probe gives for an axis that
            // never moved, reached one stage later.
            var m = Healthy() with { ForwardResponse = 0.0, ReverseResponse = 0.0 };

            var act = () => OapaCalibrationVerdicts.Derive(m, "Y");

            act.Should().Throw<InvalidOperationException>().WithMessage("*neither direction produced measurable motion*");
        }

        [Test]
        public void SymmetricAxis_CollapsesThePairToTheMean_AndScalesByTheMeanResponse() {
            var d = OapaCalibrationVerdicts.Derive(Healthy(), "test");

            d.Result.DirectionalBacklash.Should().BeFalse("equal transitions cannot establish a direction split");
            d.Result.BacklashArcmin.Should().BeApproximately(5.0f, 0.01f);
            d.Result.BacklashEnteringPositiveArcmin.Should().Be(d.Result.BacklashEnteringNegativeArcmin,
                "a non-directional verdict must report one value, not a noise-made difference");
            d.Result.Ratio.Should().BeApproximately(100f, 0.01f);
            d.Result.BacklashSuspect.Should().BeFalse();
            d.Result.ResponseSuspect.Should().BeFalse();
            d.Result.Asymmetric.Should().BeFalse();
        }

        [Test]
        public void EstablishedDirectionalPair_IsReportedPerDirection_InWireSign() {
            // Field pair 53.4'/16.2' (repeated as 59.9'/15.9' on the next run of the same
            // axis): both transitions measurable, split far beyond noise -> directional.
            var m = Healthy() with {
                BacklashLegArcmin = 60f,
                ReversalTravelArcmin = 6.6,    // S3: 60*1.0 - 6.6 = 53.4' entering -DirSign
                OppositeTravelArcmin = 43.8    // S5: 60*1.0 - 43.8 = 16.2' entering +DirSign
            };

            var d = OapaCalibrationVerdicts.Derive(m, "test");

            d.Result.DirectionalBacklash.Should().BeTrue();
            d.Result.BacklashEnteringPositiveArcmin.Should().BeApproximately(16.2f, 0.01f, "S5 measured entering +DirSign");
            d.Result.BacklashEnteringNegativeArcmin.Should().BeApproximately(53.4f, 0.01f, "S3 measured entering -DirSign");
        }

        [Test]
        public void ReverseFlag_SwapsWhichTransitionBelongsToWhichCommandedSign() {
            var m = Healthy() with {
                DirSign = -1f,
                BacklashLegArcmin = 60f,
                ReversalTravelArcmin = 6.6,
                OppositeTravelArcmin = 43.8
            };

            var d = OapaCalibrationVerdicts.Derive(m, "test");

            d.Result.BacklashEnteringPositiveArcmin.Should().BeApproximately(53.4f, 0.01f,
                "with Reverse on, the S3 transition is the one entering the positive commanded sign");
            d.Result.BacklashEnteringNegativeArcmin.Should().BeApproximately(16.2f, 0.01f);
        }

        [Test]
        public void ZeroAgainstLarge_IsASlipSignature_NotADirectionalVerdict() {
            // Field case: the same axis measured 4.10'/4.31' and, five minutes later,
            // 0.00'/8.69' - stable sum, flipped split - and the phantom pair threw a 23"
            // residual to 6'32" at the finish line.
            var m = Healthy() with {
                ReversalTravelArcmin = 45.0,   // S3: raw 0.00'
                OppositeTravelArcmin = 36.31   // S5: raw 8.69'
            };

            var d = OapaCalibrationVerdicts.Derive(m, "test");

            d.Result.DirectionalBacklash.Should().BeFalse("a zero side cannot establish the split");
            d.Result.BacklashEnteringPositiveArcmin.Should().BeApproximately(4.345f, 0.01f, "the mean is the safe collapse");
            d.Result.BacklashEnteringNegativeArcmin.Should().BeApproximately(4.345f, 0.01f);
            d.Result.BacklashSuspect.Should().BeFalse("the pair is usable, only its split is not");
        }

        [Test]
        public void NegativeTransition_InvalidatesThePair_AndReportsZeroWithTheSuspectFlag() {
            // A reversal that travels further than the response predicts is impossible as
            // play; the pair shares its inputs, so both go down together.
            var m = Healthy() with {
                ReversalTravelArcmin = 50.0,   // S3: raw -5' - beyond -threshold
                OppositeTravelArcmin = 36.0    // S5: raw +9' - looks plausible, is not
            };

            var d = OapaCalibrationVerdicts.Derive(m, "test");

            d.Result.BacklashSuspect.Should().BeTrue();
            d.Result.BacklashArcmin.Should().Be(0, "zero under-shoots and the correction loop recovers; a made-up number over-shoots");
            d.Result.BacklashEnteringPositiveArcmin.Should().Be(0);
            d.Result.BacklashEnteringNegativeArcmin.Should().Be(0);
            d.Result.DirectionalBacklash.Should().BeFalse();
        }

        [Test]
        public void ResponsesDisagreeingBeyondTwofold_MakeTheStrongDirectionTheScale() {
            // Field case: responses 0.860/0.102 blended into a factor three times what the
            // axis delivered; the strong direction alone was within a few percent.
            var m = Healthy() with {
                ForwardResponse = 0.860,
                ReverseResponse = 0.102
            };

            var d = OapaCalibrationVerdicts.Derive(m, "test");

            d.Result.ResponseSuspect.Should().BeTrue();
            d.Result.Ratio.Should().BeApproximately(100f / 0.860f, 0.01f, "the weaker direction is losing motion mechanically, not measuring the scale");
            d.Result.BacklashSuspect.Should().BeTrue("each transition is evaluated against the other direction's response");
            d.Result.BacklashArcmin.Should().Be(0);
            d.Result.Asymmetric.Should().BeTrue();
        }

        [Test]
        public void TransitionsWithinNoise_ReportZeroBacklash_WithoutSuspicion() {
            var m = Healthy() with {
                ReversalTravelArcmin = 44.7,   // raw 0.3' < 2*threshold
                OppositeTravelArcmin = 44.6    // raw 0.4'
            };

            var d = OapaCalibrationVerdicts.Derive(m, "test");

            d.Result.BacklashArcmin.Should().Be(0);
            d.Result.BacklashSuspect.Should().BeFalse("nothing measurable is not the same as something impossible");
            d.Result.DirectionalBacklash.Should().BeFalse();
        }

        [Test]
        public void ModerateResponseDisagreement_FlagsAsymmetry_ButKeepsTheMeanScale() {
            var m = Healthy() with {
                ForwardResponse = 1.0,
                ReverseResponse = 0.85,
                ReversalTravelArcmin = 33.25,  // raw 45*0.85 - 33.25 = 5'
                OppositeTravelArcmin = 40.0    // raw 45*1.0 - 40.0 = 5'
            };

            var d = OapaCalibrationVerdicts.Derive(m, "test");

            d.Result.Asymmetric.Should().BeTrue();
            d.Result.ResponseSuspect.Should().BeFalse("15% is a real asymmetry but the mean is still a compromise, not a lie");
            d.Result.Ratio.Should().BeApproximately((float)(100.0 / 0.925), 0.01f);
            d.Result.ForwardRatio.Should().BeApproximately(100f, 0.01f);
            d.Result.ReverseRatio.Should().BeApproximately((float)(100.0 / 0.85), 0.1f);
        }
    }
}
