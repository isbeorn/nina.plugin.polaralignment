using NINA.Core.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>Outcome of calibrating one axis, including the auto-reverse retry.</summary>
    public sealed class AxisCalibrationOutcome {
        public float Ratio { get; init; }
        public float BacklashArcmin { get; init; }
        public bool Consistent { get; init; }
        public bool Asymmetric { get; init; }
        public float NoiseSigmaArcmin { get; init; }
        public float ForwardRatio { get; init; }
        public float ReverseRatio { get; init; }
        public float BacklashEnteringPositiveArcmin { get; init; }
        public float BacklashEnteringNegativeArcmin { get; init; }
        public bool DirectionalBacklash { get; init; }
        /// <summary>True when the backlash pair was not measurable and both directions were zeroed.</summary>
        public bool BacklashSuspect { get; init; }
        /// <summary>True when the forward and reverse responses disagree by more than a factor of two.</summary>
        public bool ResponseSuspect { get; init; }
        /// <summary>True when the Reverse flag had to be flipped (and the flip verified) to obtain a consistent result.</summary>
        public bool Flipped { get; init; }
        /// <summary>True only when the closing moves verifiably returned the axis to its baseline (residual within tolerance).</summary>
        public bool RestoredToBaseline { get; init; }
        /// <summary>Residual against the baseline after the closing moves, in axis arcminutes; NaN when it could not be measured.</summary>
        public float ClosingResidualArcmin { get; init; }
        /// <summary>
        /// True when the clean legs measured the response on far less sky than the sequence
        /// aims for, because the command cap or the break-away floor sized them instead of the
        /// response. Reachable on any first calibration of a high-reduction axis, where the
        /// factory factor of 1 makes even the largest leg a fraction of an arcminute: the
        /// factor is the best available and usable, but calibrating again with it in place
        /// measures on the full signal. A consumer deciding how far to trust this axis - how
        /// large a correction to allow it in one move - should read this before deciding.
        /// </summary>
        public bool FactorProvisional { get; init; }
    }

    /// <summary>
    /// Orchestrates the OAPA self-calibration against injected motion and capture/solve
    /// boundaries. Owns no UI state: progress is reported through a callback and results
    /// are returned as typed values.
    ///
    /// The sequence is staged and self-scaling, so it survives grossly wrong initial
    /// ratios and backlash larger than its own measuring legs:
    ///  S0 solve noise (no motion) -> detection threshold;
    ///  S1 engagement probe, escalating until the motion is measurable;
    ///  S2 clean forward legs (post-engagement, backlash-free) -> forward response;
    ///  S3 backlash leg, escalated until the shortfall is a minority of the leg;
    ///  S4 clean reverse legs -> reverse response, asymmetry flag;
    ///  S5 opposite transition -> second backlash measure, directionality verdict;
    ///  S6 iterative close of the loop against the baseline solve.
    /// On failure or cancellation the restore is measured against the baseline too,
    /// falling back to the commanded sum only when solving is unavailable.
    /// </summary>
    public sealed class OapaCalibrationService {
        private readonly IOapaCalibrationMotion motion;
        private readonly IOapaCalibrationSolver solver;
        private readonly float calibrationStepArcmin;
        private readonly TimeSpan settleTime;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        public OapaCalibrationService(
            IOapaCalibrationMotion motion,
            IOapaCalibrationSolver solver,
            float calibrationStepArcmin = 45.0f,
            TimeSpan settleTime = default,
            Func<TimeSpan, CancellationToken, Task> delay = null) {

            // The step is the scale every bound in the sequence is expressed in: the
            // single-command cap is three of them and the travel budget four. A zero or
            // negative one collapses both to zero or below, which reads as "every leg is
            // capped to nothing" - the axis is commanded zero, nothing moves, and the pass
            // fails blaming the clutch. Refuse it here, where the cause is still visible.
            if (!float.IsFinite(calibrationStepArcmin) || calibrationStepArcmin <= 0) {
                throw new ArgumentOutOfRangeException(nameof(calibrationStepArcmin), calibrationStepArcmin,
                    "The calibration step must be a positive number of arcminutes.");
            }

            this.motion = motion;
            this.solver = solver;
            this.calibrationStepArcmin = calibrationStepArcmin;
            this.settleTime = settleTime;
            this.delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        }

        /// <summary>
        /// Every move goes through here so none can be added later without its settle.
        ///
        /// A high-friction axis keeps relaxing after the controller reports idle - one
        /// tester watched the position creep for about a second after every stop. Capturing
        /// into that relaxation measures a moving target: the response reads short and the
        /// two backlash transitions disagree for a reason that has nothing to do with the
        /// mechanism. The correction loop has waited between move and solve since it
        /// existed; the calibration never did.
        /// </summary>
        private async Task MoveAndSettle(Axis axis, float arcmin, CancellationToken token) {
            // The last line of defence, and the cheapest one. Every quantity in the sequence
            // descends from a solved sample, and a non-finite one travels through arithmetic
            // that looks guarded: Math.Max(0, NaN) is NaN, Math.Clamp(NaN, lo, hi) is NaN,
            // and every comparison against NaN is false - so each bound takes its "no" branch
            // and the value arrives here intact. Refusing it at the single point where
            // commands leave for the hardware makes the whole class of arithmetic slip
            // unable to move the platform, whatever a future stage computes.
            if (!float.IsFinite(arcmin)) {
                throw new InvalidOperationException(
                    $"{axis}: refusing to command a move of {arcmin}; a measurement that is not a number reached the motion layer");
            }
            await motion.MoveRelative(axis, arcmin, token).ConfigureAwait(false);
            if (settleTime > TimeSpan.Zero) {
                await delay(settleTime, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Calibrates an axis. If the first pass shows direction inconsistency, retries once
        /// with the direction flipped; a passing retry reports <see cref="AxisCalibrationOutcome.Flipped"/>
        /// so the caller can persist the corrected Reverse flag.
        /// </summary>
        public async Task<AxisCalibrationOutcome> CalibrateAxisWithAutoReverse(
            Axis axis, float currentRatio, bool reversed,
            string axisLabel, Action<string> reportStatus, CancellationToken token) {

            var first = await CalibrateAxis(axis, currentRatio, reversed, axisLabel, reportStatus, token).ConfigureAwait(false);
            if (first.Result.Consistent) {
                return ToOutcome(first, flipped: false, first.Result.RestoredToBaseline, first.Result.ClosingResidualArcmin);
            }

            Logger.Info($"OAPA cal {axisLabel}: direction inconsistent, retrying with Reverse flipped ({reversed} -> {!reversed})");
            reportStatus?.Invoke($"{axisLabel}: auto-flipping Reverse and retrying...");

            var second = await CalibrateAxis(axis, currentRatio, !reversed, axisLabel, reportStatus, token).ConfigureAwait(false);

            // Two passes, two different questions. The measurements come from the pass whose
            // direction verdict we keep; the physical state comes from the pass that moved the
            // axis last, because that is where the platform actually is. Reporting the first
            // pass's restore after a second pass has run says "back at your starting position"
            // about a position two passes old.
            //
            // And the claim needs both: each pass measures its baseline at its own S0, so a
            // first pass that did not verifiably come home leaves the second pass measuring
            // against an already displaced start. The second can then close its own loop
            // perfectly and still be nowhere near where the user began.
            var restoredThroughout = first.Result.RestoredToBaseline && second.Result.RestoredToBaseline;
            if (!restoredThroughout && (first.Result.RestoredToBaseline || second.Result.RestoredToBaseline)) {
                Logger.Warning($"OAPA cal {axisLabel}: one of the two passes did not return the axis to its baseline " +
                    $"(first {first.Result.ClosingResidualArcmin:F2}', second {second.Result.ClosingResidualArcmin:F2}'); " +
                    "the platform is not verifiably back where the calibration started");
            }

            if (second.Result.Consistent) {
                Logger.Info($"OAPA cal {axisLabel}: auto-flip succeeded, ratio={second.Result.Ratio:F2}");
                return ToOutcome(second, flipped: true, restoredThroughout, second.Result.ClosingResidualArcmin);
            }

            Logger.Warning($"OAPA cal {axisLabel}: auto-flip did not resolve inconsistency; keeping original Reverse={reversed}");
            return ToOutcome(first, flipped: false, restoredThroughout, second.Result.ClosingResidualArcmin);
        }

        /// <summary>
        /// One pass's outcome: the derived result, plus what only the pass itself knows about
        /// how the measurement went. <see cref="AxisCalibrationResult"/> carries the verdicts;
        /// the confidence in the signal they were derived from belongs to the run.
        /// </summary>
        private readonly record struct CalibrationPass(AxisCalibrationResult Result, bool FactorProvisional);

        private static AxisCalibrationOutcome ToOutcome(CalibrationPass pass, bool flipped,
            bool restoredToBaseline, float closingResidualArcmin) => ToOutcome(
                pass.Result, flipped, restoredToBaseline, closingResidualArcmin, pass.FactorProvisional);

        private static AxisCalibrationOutcome ToOutcome(AxisCalibrationResult r, bool flipped,
            bool restoredToBaseline, float closingResidualArcmin, bool factorProvisional) => new() {
            Ratio = r.Ratio,
            BacklashArcmin = r.BacklashArcmin,
            Consistent = r.Consistent,
            Asymmetric = r.Asymmetric,
            NoiseSigmaArcmin = r.NoiseSigmaArcmin,
            ForwardRatio = r.ForwardRatio,
            ReverseRatio = r.ReverseRatio,
            BacklashEnteringPositiveArcmin = r.BacklashEnteringPositiveArcmin,
            BacklashEnteringNegativeArcmin = r.BacklashEnteringNegativeArcmin,
            DirectionalBacklash = r.DirectionalBacklash,
            BacklashSuspect = r.BacklashSuspect,
            ResponseSuspect = r.ResponseSuspect,
            Flipped = flipped,
            RestoredToBaseline = restoredToBaseline,
            ClosingResidualArcmin = closingResidualArcmin,
            FactorProvisional = factorProvisional
        };

        private async Task<CalibrationPass> CalibrateAxis(
            Axis axis, float currentRatio, bool reversed,
            string axisLabel, Action<string> reportStatus, CancellationToken token) {

            bool isAzimuth = axis == Axis.XAxis;
            float dirSign = reversed ? -1f : 1f;
            int solveCount = 0;
            float movedArcmin = 0f;
            // Whether the axis has physically left its baseline. Deliberately NOT the
            // commanded sum: with backlash the sum returns to zero while the mechanism is
            // still displaced (the reversal legs lose motion the forward legs delivered),
            // so a failure at that exact moment used to skip the restore and leave the
            // platform off its starting position. Once any move has been commanded, only a
            // verified restore may clear the need for one.
            var needsRestore = false;
            // Whether any move has produced a measured response yet. Until it has, an
            // overshoot is a statement about the configured factor rather than about the axis.
            var responseKnown = false;
            CalibrationSolveSample last = default;
            CalibrationSolveSample baseline = default;

            // Freeze the solver's topocentric epoch for this pass: displacements between
            // samples must measure axis motion only, not the sidereal drift of a tracked
            // field's Alt/Az between solve times. Field signature of the aliasing: closing
            // residuals against a minutes-old baseline that no iteration could remove
            // (0.6' on one rig, 5' on a slower one).
            solver.BeginCalibration();

            async Task<CalibrationSolveSample> NextSolve() {
                if (++solveCount > MaxSolvesPerAxis) {
                    throw new InvalidOperationException(
                        $"{axisLabel}: calibration exceeded its solve budget ({MaxSolvesPerAxis} solves); aborting to limit the sky excursion");
                }
                var s = await solver.CaptureAndSolve(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // Reject a non-finite sample where it enters rather than where it hurts. A
                // solve that reports coordinates that are not numbers is a solver or driver
                // defect, but the calibration is what would turn it into motion: everything
                // below - the noise floor, the detection threshold, the responses, the
                // backlash pair, the closing moves - is arithmetic on these two numbers.
                // Failing here names the cause; letting it through produces a pass that
                // blames the clutch, or a factor and a backlash of NaN applied to the axis.
                if (!double.IsFinite(s.AltitudeDegrees) || !double.IsFinite(s.AzimuthDegrees)) {
                    throw new InvalidOperationException(
                        $"{axisLabel}: the plate solve reported coordinates that are not numbers " +
                        $"(altitude {s.AltitudeDegrees}, azimuth {s.AzimuthDegrees}); calibration cannot measure against them");
                }
                return s;
            }

            // Moves by a positive logical amount in the given logical direction and returns
            // the signed physical displacement measured by the next solve.
            async Task<double> MoveAndMeasure(float logicalArcmin, float logicalDirection) {
                var wire = dirSign * logicalDirection * logicalArcmin;
                needsRestore = true; // set before the move: a failure mid-motion leaves the position unknown
                await MoveAndSettle(axis, wire, token).ConfigureAwait(false);
                movedArcmin += wire;
                var now = await NextSolve().ConfigureAwait(false);
                var d = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, last, now);
                last = now;

                // Travel budget, measured on the sky rather than on the commanded sum: a
                // wrong factor makes commanded arcminutes meaningless as a bound, but the
                // solve always knows how far from the start the axis really is. This is
                // the backstop for the pathological cases the self-scaling legs cannot
                // absorb (stick-slip that fools the probe, a runaway response) before the
                // platform is driven toward its mechanical travel limit.
                var excursion = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, now);
                if (Math.Abs(excursion) > TravelBudgetArcmin) {
                    // Before any response is known this is the mirror image of the failed
                    // engagement: the first probe is the one move in the sequence sized from no
                    // measurement at all, so a factor far above the truth spends the whole
                    // travel budget in a single command. Telling that user to check that the
                    // axis moves freely is the opposite of the advice they need.
                    if (!responseKnown) {
                        var impliedRatio = currentRatio * logicalArcmin / Math.Abs(d);
                        throw new InvalidOperationException(
                            $"{axisLabel}: the first {logicalArcmin:F0}' probe moved the axis {d:F0}', past the " +
                            $"±{TravelBudgetArcmin:F0}' travel budget in one command. This looks like a calibration factor far " +
                            $"above the truth: roughly {impliedRatio:F0} units per arcminute against the {currentRatio:F0} " +
                            "configured. Enter an approximate value in the panel and calibrate again.");
                    }
                    throw new InvalidOperationException(
                        $"{axisLabel}: calibration exceeded its travel budget ({excursion:F0}' from the start, budget ±{TravelBudgetArcmin:F0}'); " +
                        "aborting to protect the axis travel. Check that the axis moves freely and that the factor is plausible.");
                }
                return d;
            }

            // S0: solve noise with the axis at rest; everything below detects motion
            // against a threshold derived from it (fail fast on an unsolvable field too).
            reportStatus?.Invoke($"{axisLabel}: measuring solve noise...");
            var s0a = await NextSolve().ConfigureAwait(false);
            baseline = await NextSolve().ConfigureAwait(false);
            var noise = Math.Abs(OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, s0a, baseline));
            var threshold = Math.Max(NoiseSigmaFactor * noise, DetectionFloorArcmin);
            last = baseline;

            // S0 cannot tell noise from motion: it measures the difference between two solves
            // with the axis at rest and calls it noise. Plate-solve noise on the field rigs
            // runs 0.07'-0.13', so an arcminute of it is not the solver - it is the sky moving,
            // and every threshold below inherits it. Said out loud here because it degrades
            // the whole pass quietly: the backlash resolution is two thresholds wide.
            if (noise > FieldMotionNoiseArcmin) {
                Logger.Warning($"OAPA cal {axisLabel}: the field moved {noise:F2}' between two solves with the axis at rest - " +
                    "far more than plate-solve noise. The mount may not be tracking or may still be settling; " +
                    $"everything measured in this pass is judged against a {threshold:F2}' threshold because of it");
            }

            if (isAzimuth && Math.Cos(baseline.AltitudeDegrees * Math.PI / 180.0) < OapaCalibrationGeometry.MinimumAzimuthCosAltitude) {
                throw new InvalidOperationException(
                    $"{axisLabel}: field altitude {baseline.AltitudeDegrees:F0}° is too close to the zenith for azimuth calibration. " +
                    "Point the scope at a lower altitude (ideally toward the celestial pole) and retry.");
            }

            // The altitude actuator tilts the rig about the horizontal east-west axis; a field at
            // azimuth A only shows cos(A) of that tilt in its altitude. Near due east/west there is
            // no signal to measure, and a factor calibrated on the residual projection is inflated
            // by 1/|cos(A)| — three field sessions on one rig produced 97.5/202.7/255.0 for a true
            // factor of 73-85 this way. Refusing here, before any motion, beats measuring a number
            // that will oscillate or stall the correction loop later.
            var baselineCosAz = Math.Cos(baseline.AzimuthDegrees * Math.PI / 180.0);
            if (!isAzimuth && Math.Abs(baselineCosAz) < OapaCalibrationGeometry.MinimumAltitudeCosAzimuth) {
                throw new InvalidOperationException(
                    $"{axisLabel}: field azimuth {baseline.AzimuthDegrees:F0}° is too close to due east/west for altitude calibration — " +
                    $"only {Math.Abs(baselineCosAz):P0} of the axis motion is visible there. " +
                    "Point the scope toward the meridian or the celestial pole and retry.");
            }

            try {
                // S1: engage the drive train and find the rough scale. The probe escalates
                // until the measured motion clears the noise threshold.
                float probe = InitialProbeArcmin;
                double roughResponse = 0;
                var engagedProbe = 0f;
                var engaged = false;
                var largestProbe = 0f;
                var largestProbeTravel = 0.0;
                for (var attempt = 0; attempt < MaxEngageAttempts; attempt++) {
                    reportStatus?.Invoke($"{axisLabel}: probing +{probe:F0}'...");
                    var d = await MoveAndMeasure(probe, +1f).ConfigureAwait(false);
                    if (Math.Abs(d) >= threshold) {
                        roughResponse = Math.Abs(d) / probe;
                        engagedProbe = probe;
                        responseKnown = true;
                        engaged = true;
                        break;
                    }
                    // Kept even though the probe failed: a displacement below the threshold is
                    // still a measurement, and it is the only evidence that separates "this axis
                    // does not move" from "these units are far too small to move it measurably".
                    largestProbe = probe;
                    largestProbeTravel = Math.Abs(d);
                    Logger.Info($"OAPA cal {axisLabel}: probe {probe:F0}' moved only {d:F2}' (threshold {threshold:F2}'), escalating");
                    probe *= EngageEscalationFactor;
                }
                if (!engaged) {
                    throw new InvalidOperationException(
                        DescribeEngagementFailure(axisLabel, noise, threshold, largestProbe, largestProbeTravel, currentRatio));
                }

                // S2: clean forward legs. Post-engagement, same direction: backlash-free by
                // construction. The leg size targets a fixed physical displacement so the
                // measurement scales itself to whatever the current ratio error is.
                // The leg self-scales to the measured response, so a barely-responding
                // probe would ask for an unbounded logical leg (8'/0.04 = 200'). Cap every
                // single commanded leg at the same bound the closing moves already use:
                // a leg that large means the response measurement is not to be trusted,
                // not that the axis should be driven three degrees in one command.
                //
                // The floor matters as much as the cap: on a rig with real break-away
                // friction the probe escalates until something moves, and a leg smaller than
                // the command that was just proven to move the axis moves nothing at all.
                // Both clean legs then read zero and the pass fails as "no measurable
                // motion" - blaming the clutch and the motor current for a leg the sequence
                // itself sized too small. Never ask for less than what visibly worked.
                var legLogical = (float)Math.Min(SingleCommandCapArcmin,
                    Math.Max(engagedProbe, Math.Max(1.0, TargetCleanLegPhysicalArcmin / roughResponse)));
                // Travel feasibility, decided before the first measuring leg instead of
                // discovered halfway through it.
                //
                // A leg's physical size is leg x response. When the response sizes the leg
                // that product is about 8' by construction, whatever the factor - but both
                // bounds on the leg are expressed in *logical* units, and a logical leg
                // multiplied by a healthy response is a large physical move. That happens
                // on an axis with real break-away friction: the probe had to grow to move
                // it at all, the floor keeps the legs at least that large, and the sequence
                // then needs the probe plus two same-direction legs before it ever reverses.
                //
                // Left to the per-move check, such a rig aborts mid-sequence blaming the sky
                // excursion - pointing the user at a runaway that never happened. The cause
                // is the mechanism's break-away command, and it is knowable here.
                // Scoped to the cause it can actually name: the probe having had to grow
                // before anything moved is what break-away friction looks like from here. An
                // axis that engaged on the first probe and still cannot fit its legs has a
                // response so large that even the smallest probe travels too far - a runaway
                // or a badly wrong factor - and that abort belongs to the per-move budget
                // check, which says so in its own terms. Attributing it to friction here
                // would point the user at the wrong mechanism just as convincingly.
                var probeTravel = Math.Abs(OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, last));
                var mandatoryTravel = probeTravel + 2 * legLogical * roughResponse;
                var brokeAwayLate = engagedProbe > InitialProbeArcmin;
                if (brokeAwayLate && mandatoryTravel > TravelBudgetArcmin) {
                    throw new InvalidOperationException(
                        $"{axisLabel}: this axis only responds to commands of about {legLogical:F0}' " +
                        $"({legLogical * roughResponse:F0}' of sky per leg), so measuring it needs about {mandatoryTravel:F0}' of travel - " +
                        $"more than the {TravelBudgetArcmin:F0}' available. Break-away friction is too large to calibrate at this " +
                        "resolution: reduce it mechanically, or raise the calibration step so the budget grows with it.");
                }
                if (probeTravel + 3 * legLogical * roughResponse > TravelBudgetArcmin) {
                    Logger.Warning($"OAPA cal {axisLabel}: the travel budget has room for two clean legs but not a third; " +
                        "if the two disagree the sequence cannot add one to break the tie");
                }

                reportStatus?.Invoke($"{axisLabel}: forward legs ({legLogical:F0}')...");
                var beforeLeg = last;
                var f1 = await MoveAndMeasure(legLogical, +1f).ConfigureAwait(false);
                // A sign is only evidence when there is motion to take the sign of. Math.Sign(0)
                // is zero, which matches no commanded sign, so a leg that delivered nothing used
                // to read as "wired backwards" - and the remedy for that verdict is a whole second
                // pass with the direction flipped, which is the wrong remedy at double the cost.
                // Absence of motion is not evidence of inversion: it is reachable on an axis whose
                // clutch grabs intermittently, and the response verdicts already name that axis.
                var directionConsistent = Math.Abs(f1) < threshold
                    || OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuth, beforeLeg, last, legLogical);
                var f2 = await MoveAndMeasure(legLogical, +1f).ConfigureAwait(false);
                double forwardResponse;
                // Two legs that both read zero are not a disagreement to resolve with a
                // third leg: they are a dead axis, and 0/0 would carry a NaN into every
                // comparison below (NaN fails every test silently, so the third leg would
                // be skipped for the wrong reason). Let the verdict layer name the failure.
                var largestLeg = Math.Max(Math.Abs(f1), Math.Abs(f2));
                var spread = largestLeg > 0 ? Math.Abs(Math.Abs(f1) - Math.Abs(f2)) / largestLeg : 0.0;
                if (spread > CleanLegSpreadThreshold) {
                    Logger.Info($"OAPA cal {axisLabel}: forward legs spread {spread:P0}, adding a third leg");
                    var f3 = await MoveAndMeasure(legLogical, +1f).ConfigureAwait(false);
                    forwardResponse = Median(Math.Abs(f1), Math.Abs(f2), Math.Abs(f3)) / legLogical;
                } else {
                    forwardResponse = (Math.Abs(f1) + Math.Abs(f2)) / 2.0 / legLogical;
                }

                // How much sky the legs actually covered, against how much they aim for. The
                // two differ only when something other than the response sized the leg - the
                // command cap, or the break-away floor - and then the response was measured on
                // a fraction of the intended signal. Nothing else in the pass notices: both
                // directions agree with each other, so no suspect flag fires; they simply agree
                // on a number measured too close to the noise.
                var achievedLegArcmin = legLogical * forwardResponse;
                var factorProvisional = achievedLegArcmin < ProvisionalSignalFraction * TargetCleanLegPhysicalArcmin;
                if (factorProvisional) {
                    Logger.Warning($"OAPA cal {axisLabel}: the clean legs covered {achievedLegArcmin:F2}' of sky against the " +
                        $"{TargetCleanLegPhysicalArcmin:F0}' they aim for (leg {legLogical:F0}' at {forwardResponse:F4} '/unit, " +
                        $"noise {noise:F2}'); the factor is usable but provisional - calibrating again with it in place " +
                        "lets the legs reach their intended size");
                }

                // S3: backlash leg with escalation. A reversal that comes up short by more
                // than half its own expected travel was mostly eaten by the backlash: the
                // measure is contaminated, so the leg grows and the transition is repeated
                // until the shortfall is a minority share (this is what survives backlash
                // larger than the initial leg).
                var backlashLeg = legLogical;
                double reversalTravel = 0;
                for (var i = 0; ; i++) {
                    reportStatus?.Invoke($"{axisLabel}: reversal leg -{backlashLeg:F0}'...");
                    var d = await MoveAndMeasure(backlashLeg, -1f).ConfigureAwait(false);
                    reversalTravel = Math.Abs(d);
                    var expected = backlashLeg * forwardResponse;
                    var shortfall = Math.Max(0, expected - reversalTravel);
                    if (shortfall <= BacklashLegFraction * expected) {
                        break;
                    }
                    if (i >= MaxBacklashEscalations - 1) {
                        Logger.Warning($"OAPA cal {axisLabel}: backlash still dominates the {backlashLeg:F0}' leg after escalation; the value may be underestimated");
                        break;
                    }
                    // Two bounds, and both are needed. The physical one keeps the sky
                    // excursion sane when the response is right; the absolute one keeps the
                    // command sane when it is not - and S3 exists precisely because S2's
                    // measurement can be contaminated. A response read ten times low turns
                    // "90' of sky" into 900' of it, so the escalated leg respects the same
                    // absolute cap as the clean legs.
                    var nextLeg = (float)(backlashLeg + 2.0 * shortfall / forwardResponse);
                    nextLeg = (float)Math.Min(nextLeg, Math.Min(MaxLegPhysicalArcmin / forwardResponse, SingleCommandCapArcmin));
                    if (nextLeg <= backlashLeg) {
                        Logger.Warning($"OAPA cal {axisLabel}: the backlash leg cannot grow past the single-command cap ({SingleCommandCapArcmin:F0}'); accepting the measure, which may be underestimated");
                        break;
                    }
                    Logger.Info($"OAPA cal {axisLabel}: reversal lost {shortfall:F1}' of {expected:F1}'; escalating the backlash leg to {nextLeg:F0}'");
                    reportStatus?.Invoke($"{axisLabel}: backlash exceeds the leg, re-measuring at {nextLeg:F0}'...");
                    await MoveAndMeasure(nextLeg, +1f).ConfigureAwait(false); // re-engage forward; not a clean sample
                    backlashLeg = nextLeg;
                }

                // S4: clean reverse legs (engaged reverse after S3).
                reportStatus?.Invoke($"{axisLabel}: reverse legs ({legLogical:F0}')...");
                var r1 = await MoveAndMeasure(legLogical, -1f).ConfigureAwait(false);
                var r2 = await MoveAndMeasure(legLogical, -1f).ConfigureAwait(false);
                var reverseResponse = (Math.Abs(r1) + Math.Abs(r2)) / 2.0 / legLogical;

                // S5: opposite transition, the second backlash quantity.
                reportStatus?.Invoke($"{axisLabel}: measuring the opposite transition...");
                var dBack = await MoveAndMeasure(backlashLeg, +1f).ConfigureAwait(false);

                // Everything measured; what the measurements *mean* — the suspect flags,
                // the directionality verdict, the scale choice — is the pure derivation.
                var derivation = OapaCalibrationVerdicts.Derive(new AxisCalibrationMeasurements(
                    CurrentRatio: currentRatio,
                    DirSign: dirSign,
                    NoiseSigmaArcmin: noise,
                    DetectionThresholdArcmin: threshold,
                    DirectionConsistent: directionConsistent,
                    ForwardResponse: forwardResponse,
                    ReverseResponse: reverseResponse,
                    BacklashLegArcmin: backlashLeg,
                    ReversalTravelArcmin: reversalTravel,
                    OppositeTravelArcmin: Math.Abs(dBack)), axisLabel);
                var result = derivation.Result;

                // Both the raw pair and the pair that will actually be applied: they differ
                // whenever the directionality verdict collapses them, and a log that only
                // showed one of the two is what made this take three field sessions to find.
                // The pointing and its projection make a future log self-diagnosing: a factor
                // measured through a foreshortened projection is visible right where it was born.
                Logger.Info($"OAPA cal {axisLabel}: noise={noise:F2}', responses fwd={forwardResponse:F3}/rev={reverseResponse:F3} '/unit, " +
                    $"backlash={derivation.BacklashForwardArcmin:F2}'/{derivation.BacklashReverseArcmin:F2}' -> applied +{result.BacklashEnteringPositiveArcmin:F2}'/-{result.BacklashEnteringNegativeArcmin:F2}', ratio={result.Ratio:F2}, " +
                    $"consistent={result.Consistent}, asymmetric={result.Asymmetric}, directional={result.DirectionalBacklash}, " +
                    $"backlashSuspect={result.BacklashSuspect}, responseSuspect={result.ResponseSuspect}, solves={solveCount}, " +
                    $"field alt={baseline.AltitudeDegrees:F1}/az={baseline.AzimuthDegrees:F1}, proj={(isAzimuth ? 1.0 : baselineCosAz):F3}");

                // S6: physically return to the baseline. The response just measured makes
                // the closing moves exact; iterating covers the backlash a closing reversal
                // eats on its first move. The outcome is carried on the result: a
                // calibration that measured fine but did not verifiably return home must
                // say so, not report full success.
                // The response the measurement agreed on, not the first leg: S2 adds a third
                // leg and takes the median precisely when it decides f1 cannot be trusted,
                // and sizing every closing move from the discarded sample would put the
                // rejected measurement back in charge of the moves that bring the platform
                // home. Only the direction comes from the legs; the magnitude is the agreed one.
                var forwardSign = Math.Sign(f1 + f2);
                var responsePerWire = forwardSign * forwardResponse / dirSign;
                if (Math.Abs(responsePerWire) < 1e-3) {
                    // The forward legs produced no usable scale - an axis that grabs
                    // intermittently makes the median of three legs zero - but the reverse
                    // ones did, and the closing moves need a magnitude, not a preference for
                    // one direction. Refusing here would leave the platform wherever the
                    // reverse legs put it, with nothing but a warning, while a perfectly good
                    // scale sat unused. This is the rule the verdict derivation already
                    // applies to the factor itself: a direction that stalled must not decide
                    // the scale when the other one measured cleanly.
                    var reverseSign = Math.Sign(r1 + r2);
                    responsePerWire = -reverseSign * reverseResponse / dirSign;
                    Logger.Info($"OAPA cal {axisLabel}: forward legs gave no usable scale for the closing moves; using the reverse response ({reverseResponse:F3} '/unit)");
                }
                var (restored, closingResidual) = await CloseLoopAgainstBaseline(axis, isAzimuth, baseline, responsePerWire, threshold, axisLabel, reportStatus, NextSolve, token).ConfigureAwait(false);
                result.RestoredToBaseline = restored;
                result.ClosingResidualArcmin = closingResidual;

                return new CalibrationPass(result, factorProvisional);
            } catch (Exception) when (needsRestore) {
                await BestEffortRestore(axis, isAzimuth, baseline, movedArcmin, axisLabel).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Drives the measured residual against the baseline back to zero, up to
        /// <see cref="MaxClosingIterations"/> moves: the first closing reversal loses the
        /// backlash, the following iteration completes the travel. A failed closing move
        /// never discards the calibration result, but it must not be silent either: the
        /// returned pair says whether the axis is verifiably back home (residual measured
        /// and within tolerance) and what that residual was (NaN when unmeasurable).
        /// Cancellation is not handled here — it propagates so the caller's best-effort
        /// restore runs and the cancellation reaches the user instead of being converted
        /// into an apparent success.
        /// </summary>
        private async Task<(bool restored, float residualArcmin)> CloseLoopAgainstBaseline(
            Axis axis, bool isAzimuth, CalibrationSolveSample baseline, double responsePerWire,
            double detectionThresholdArcmin,
            string axisLabel, Action<string> reportStatus, Func<Task<CalibrationSolveSample>> nextSolve, CancellationToken token) {

            if (Math.Abs(responsePerWire) < 1e-3) {
                Logger.Warning($"OAPA cal {axisLabel}: response too small to close the loop; the axis was not returned to its baseline");
                return (false, float.NaN);
            }
            try {
                var current = await nextSolve().ConfigureAwait(false);
                var stalled = 0;
                for (var i = 0; i < MaxClosingIterations; i++) {
                    var residual = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                    if (Math.Abs(residual) < RestoreToleranceArcmin) {
                        Logger.Info($"OAPA cal {axisLabel}: closed loop against baseline; residual {residual:F2}'");
                        return (true, (float)residual);
                    }
                    var closing = (float)Math.Clamp(-residual / responsePerWire, -SingleCommandCapArcmin, SingleCommandCapArcmin);
                    reportStatus?.Invoke($"{axisLabel}: returning to start ({residual:+0.0;-0.0}' off)...");
                    await MoveAndSettle(axis, closing, token).ConfigureAwait(false);
                    current = await nextSolve().ConfigureAwait(false);

                    // On a rig with break-away friction the residual can be smaller than the
                    // smallest move the axis responds to, and the remaining iterations are
                    // then silent no-ops charged to the solve budget. But one stalled move is
                    // expected rather than futile: a closing move that reverses direction pays
                    // the backlash before it travels, which is the whole reason this loop
                    // iterates. So the first stall is allowed and only the second ends it,
                    // reporting the residual - the honest answer being that this axis cannot
                    // be positioned finer than this.
                    var after = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                    var improvement = Math.Abs(residual) - Math.Abs(after);

                    // A residual that grew is not the backlash signature: backlash costs a move
                    // its travel, it does not spend it in the opposite direction. Growth means the
                    // scale driving these moves has the wrong sign - reachable when the response it
                    // came from was measured near zero - and every further iteration would drive
                    // the platform further away. This is a stop, not a stall to be tolerated.
                    if (improvement < -detectionThresholdArcmin) {
                        Logger.Warning($"OAPA cal {axisLabel}: a closing move of {closing:F1}' moved the axis further from its baseline " +
                            $"({residual:F2}' -> {after:F2}'); stopping rather than iterating on a scale whose sign cannot be right");
                        return (false, (float)after);
                    }

                    // The travel budget bounds the measuring legs; it has to bound the way home
                    // too, or the one place free to move without it becomes the way to leave it.
                    if (Math.Abs(after) > TravelBudgetArcmin) {
                        Logger.Warning($"OAPA cal {axisLabel}: the closing moves left the axis {after:F0}' from its baseline, " +
                            $"past the {TravelBudgetArcmin:F0}' travel budget; stopping");
                        return (false, (float)after);
                    }

                    if (improvement < detectionThresholdArcmin) {
                        if (++stalled >= 2) {
                            Logger.Info($"OAPA cal {axisLabel}: two closing moves in a row left the residual at {after:F2}' " +
                                $"(last command {closing:F1}'); the axis does not respond to moves this small, stopping with it as the final residual");
                            return (Math.Abs(after) < RestoreToleranceArcmin, (float)after);
                        }
                    } else {
                        stalled = 0;
                    }
                }
                var final = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                var withinTolerance = Math.Abs(final) < RestoreToleranceArcmin;
                Logger.Info($"OAPA cal {axisLabel}: closing iterations exhausted; residual {final:F2}'" +
                    (withinTolerance ? string.Empty : " (out of tolerance - the axis is not back at its baseline)"));
                return (withinTolerance, (float)final);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                // The calibration result is already measured; a failed closing move must not discard it.
                Logger.Warning($"OAPA cal {axisLabel}: failed to close the loop against the baseline ({ex.Message})");
                return (false, float.NaN);
            }
        }

        /// <summary>
        /// Restore after a mid-sequence failure or cancellation. Driving back the commanded
        /// sum is blind to backlash, so this measures the residual against the baseline and
        /// drives it back, iterating so a restore reversal that loses its backlash still
        /// completes. Only when the solve itself is unavailable - often the reason the
        /// sequence failed - does it fall back to the commanded-sum restore.
        /// </summary>
        private async Task BestEffortRestore(Axis axis, bool isAzimuth, CalibrationSolveSample baseline, float movedArcmin, string axisLabel) {
            var cap = SingleCommandCapArcmin;
            try {
                for (var i = 0; i < MaxClosingIterations; i++) {
                    var current = await solver.CaptureAndSolve(CancellationToken.None).ConfigureAwait(false);
                    var residual = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                    if (Math.Abs(residual) < RestoreToleranceArcmin) { return; }
                    var restore = (float)Math.Clamp(-residual, -cap, cap);
                    Logger.Info($"OAPA cal {axisLabel}: failure with {movedArcmin:F1}' commanded outstanding; measured {residual:F1}' from baseline, driving back");
                    await MoveAndSettle(axis, restore, CancellationToken.None).ConfigureAwait(false);
                }
            } catch (Exception measureEx) {
                Logger.Warning($"OAPA cal {axisLabel}: measured restore unavailable ({measureEx.Message}); driving back the commanded sum");
                try {
                    // Driven back in cap-sized moves rather than as one command. The commanded sum
                    // can be several times any leg the sequence allows itself - escalated backlash
                    // legs add up - and this is the path taken when solving is unavailable, so
                    // nothing is watching the sky while it runs. The cap that bounds every measured
                    // move has to bound the blind one most of all.
                    var remaining = -movedArcmin;
                    for (var i = 0; i < MaxClosingIterations && Math.Abs(remaining) > RestoreToleranceArcmin; i++) {
                        var chunk = (float)Math.Clamp(remaining, -cap, cap);
                        await MoveAndSettle(axis, chunk, CancellationToken.None).ConfigureAwait(false);
                        remaining -= chunk;
                    }
                    if (Math.Abs(remaining) > RestoreToleranceArcmin) {
                        Logger.Warning($"OAPA cal {axisLabel}: {remaining:F0}' of the commanded sum remains undriven " +
                            $"after {MaxClosingIterations} capped moves; the axis is not back at its start");
                    }
                } catch (Exception restoreEx) {
                    Logger.Error($"OAPA cal {axisLabel}: failed to restore start position", restoreEx);
                }
            }
        }

        /// <summary>
        /// Names the cause of a failed engagement instead of blaming the hardware for all of
        /// them. The same "axis did not move measurably" covered three different faults, and
        /// two of them are software: the evidence to tell them apart is already in hand by the
        /// time the probe gives up, and the difference is a tester spending a night on a
        /// clutch that was never the problem.
        ///
        /// The implied factor comes out of the arithmetic the sequence already does. A command
        /// of L logical arcminutes is L x configured units, and the sky moves that over the
        /// true factor, so a probe of L that travelled d implies a true factor of L x
        /// configured / d. Below the detection threshold that number is rough - but an order
        /// of magnitude is exactly what someone needs to type into the panel.
        /// </summary>
        private static string DescribeEngagementFailure(string axisLabel, double noise, double threshold,
            float largestProbe, double largestProbeTravel, float currentRatio) {

            if (noise > FieldMotionNoiseArcmin) {
                return $"{axisLabel}: the field moved {noise:F2}' between two solves taken with the axis at rest, " +
                    "so nothing this axis did could be told apart from it. That is not plate-solve noise " +
                    "(0.1' is typical): check that the mount is tracking and has settled after the slew, then retry.";
            }

            // "It moved a little" is only evidence if the little is bigger than the noise: a
            // dead axis under a solver with any noise at all still reports a nonzero
            // displacement. And the implied factor has to be a factor: a mechanism needing
            // more than MaxCredibleRatio units per arcminute would be a reduction of tens of
            // thousands to one, so a number past it describes an axis that is not moving
            // rather than a gear train that is very fine.
            var movedAtAll = largestProbe > 0 && largestProbeTravel > Math.Max(noise, MinimumCredibleTravelArcmin);
            var impliedRatio = movedAtAll ? currentRatio * largestProbe / largestProbeTravel : double.PositiveInfinity;
            if (movedAtAll && impliedRatio <= MaxCredibleRatio) {
                return $"{axisLabel}: the largest probe ({largestProbe:F0}') moved the axis {largestProbeTravel:F2}', " +
                    $"below the {threshold:F2}' needed to measure it - the axis moves, the commands are simply too small. " +
                    $"This looks like a calibration factor far below the truth: roughly {impliedRatio:F0} units per arcminute " +
                    $"against the {currentRatio:F0} configured. Enter an approximate value in the panel and calibrate again.";
            }

            return $"{axisLabel}: the axis did not move measurably across probes up to {largestProbe:F0}'; " +
                "check the clutch, the motor current and that the controller is powered";
        }

        private static double Median(double a, double b, double c) {
            return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }

        /// <summary>First engagement probe, in logical arcminutes.</summary>
        private const float InitialProbeArcmin = 5.0f;
        /// <summary>Probe growth factor while the motion stays below the detection threshold.</summary>
        private const float EngageEscalationFactor = 3.0f;
        /// <summary>Probe attempts before declaring the axis immobile (5', 15', 45', 135').</summary>
        private const int MaxEngageAttempts = 4;
        /// <summary>Motion detection threshold is this many times the measured solve noise.</summary>
        private const double NoiseSigmaFactor = 5.0;
        /// <summary>Detection floor when the two noise solves happen to agree, in arcminutes.</summary>
        private const double DetectionFloorArcmin = 0.25;
        /// <summary>Physical size the clean measuring legs aim for, in axis arcminutes.</summary>
        private const double TargetCleanLegPhysicalArcmin = 8.0;
        /// <summary>Clean-leg disagreement above which a third leg and the median are used.</summary>
        private const double CleanLegSpreadThreshold = 0.10;
        /// <summary>A reversal shortfall above this share of the expected travel means the leg was mostly backlash: escalate.</summary>
        private const double BacklashLegFraction = 0.5;
        /// <summary>Maximum backlash-leg escalations before accepting the measure with a warning.</summary>
        private const int MaxBacklashEscalations = 3;
        /// <summary>
        /// Hard solve budget per axis pass, derived from the worst case the stages can
        /// legitimately reach rather than picked as a round number. A budget below that sum
        /// fires on a difficult rig that is behaving exactly as designed - and it fires
        /// wherever the last solves happen to be, which is the closing loop: the calibration
        /// would be measured, the platform left displaced, and the log would blame a sky
        /// excursion while the axis was on its way home. Exceeding this sum means the
        /// sequence itself is looping, which is worth aborting for.
        /// </summary>
        private const int MaxSolvesPerAxis =
            2                                       // S0: the noise pair
            + MaxEngageAttempts                     // S1: one solve per probe
            + 3                                     // S2: two clean legs, three when they disagree
            + (2 * MaxBacklashEscalations - 1)      // S3: a reversal per attempt, a re-engage between them
            + 2                                     // S4: reverse legs
            + 1                                     // S5: the opposite transition
            + 1 + MaxClosingIterations;             // S6: the first measure, then one per closing move
        /// <summary>Physical cap for a single escalated leg, in axis arcminutes (sky excursion guard).</summary>
        private const double MaxLegPhysicalArcmin = 90.0;
        /// <summary>Closing/restore iterations: one reversal may eat its backlash, the next completes.</summary>
        private const int MaxClosingIterations = 3;

        /// <summary>Residuals below this are indistinguishable from solve noise and left alone.</summary>
        private const float RestoreToleranceArcmin = 0.5f;

        /// <summary>
        /// Travel budget as a multiple of the calibration step: the measured excursion
        /// from the baseline may never exceed this (4 x 45' = 3° by default). Escalated
        /// backlash legs on high-play rigs legitimately reach ~2° of round trip; anything
        /// beyond this is a malfunction, not a measurement.
        /// </summary>
        private const float MaxExcursionSteps = 4f;

        /// <summary>
        /// Above this, what S0 measured between two solves with the axis at rest is not the
        /// solver. Field rigs run 0.07'-0.13' of plate-solve noise; a mount that is not
        /// tracking drifts about 15' per minute, which is arcminutes between two exposures.
        /// </summary>
        private const double FieldMotionNoiseArcmin = 1.0;

        /// <summary>
        /// Below this share of <see cref="TargetCleanLegPhysicalArcmin"/>, the clean legs
        /// measured the response on far less signal than the sequence aims for - which happens
        /// when the cap or the break-away floor decided the leg instead of the response. The
        /// factor is still the best available, but it is provisional: calibrating again with
        /// it in place lets the legs reach their intended size.
        /// </summary>
        private const double ProvisionalSignalFraction = 1.0 / 3.0;

        /// <summary>
        /// Travel below this is not evidence that the axis moved: the best plate-solve noise
        /// seen in the field is 0.07', so anything under a twentieth of an arcminute is the
        /// solver talking, whatever the axis did.
        /// </summary>
        private const double MinimumCredibleTravelArcmin = 0.05;

        /// <summary>
        /// Above this, an implied "units per arcminute" is not describing a gear train. A
        /// NEMA17 at 16 microsteps turns 3200 units per revolution, so ten thousand units per
        /// arcminute would need a reduction near 70000:1 - the number belongs to an axis that
        /// is not moving, not to a very fine mechanism.
        /// </summary>
        private const double MaxCredibleRatio = 10000.0;

        /// <summary>
        /// Absolute bound on any single commanded move, in logical arcminutes: measuring
        /// legs, escalated backlash legs, closing moves and the restore alike. Unlike the
        /// travel budget this one does not depend on a measured response, which is what
        /// makes it the guard that still holds when the response measurement is wrong.
        /// </summary>
        private float SingleCommandCapArcmin => 3f * calibrationStepArcmin;

        /// <summary>
        /// How far from the baseline the axis may travel during a pass, in axis arcminutes
        /// measured on the sky. Paired with <see cref="SingleCommandCapArcmin"/> it decides
        /// what the sequence can measure at all: the cap bounds one command in logical units,
        /// this bounds the physical result of all of them.
        /// </summary>
        private float TravelBudgetArcmin => MaxExcursionSteps * calibrationStepArcmin;
    }
}
