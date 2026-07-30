using NINA.Core.Utility;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>Motion boundary for the calibration: relative axis moves in axis arcminutes.</summary>
    public interface IOapaCalibrationMotion {
        Task MoveRelative(Axis axis, float arcmin, CancellationToken token);
    }

    /// <summary>Capture-and-solve boundary for the calibration.</summary>
    public interface IOapaCalibrationSolver {
        /// <summary>Captures an image and plate-solves it, retrying internally as configured.</summary>
        Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token);
    }

    /// <summary>Outcome of calibrating one axis, including the auto-reverse retry.</summary>
    public sealed class AxisCalibrationOutcome {
        public float Ratio { get; init; }
        public float BacklashArcmin { get; init; }
        public bool Consistent { get; init; }
        public bool Asymmetric { get; init; }
        /// <summary>True when the Reverse flag had to be flipped (and the flip verified) to obtain a consistent result.</summary>
        public bool Flipped { get; init; }
    }

    /// <summary>
    /// Orchestrates the OAPA self-calibration sequence against injected motion and
    /// capture/solve boundaries. Owns no UI state: progress is reported through a callback
    /// and results are returned as typed values.
    ///
    /// Sequence per axis: baseline solve (fail-fast before any motion), prime +S (absorbs any
    /// pending backlash), solve A, +S, solve B, -S, solve C, -S, solve D. The net commanded
    /// motion is zero but the reversal leg loses the backlash, so the sequence closes the
    /// loop against the baseline solve to physically return the axis to its start; on
    /// mid-sequence failure the restore is measured against the baseline too, falling back
    /// to the commanded sum only when solving is unavailable.
    /// </summary>
    public sealed class OapaCalibrationService {
        private readonly IOapaCalibrationMotion motion;
        private readonly IOapaCalibrationSolver solver;
        private readonly float calibrationStepArcmin;

        public OapaCalibrationService(IOapaCalibrationMotion motion, IOapaCalibrationSolver solver, float calibrationStepArcmin = 45.0f) {
            this.motion = motion;
            this.solver = solver;
            this.calibrationStepArcmin = calibrationStepArcmin;
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
            if (first.Consistent) {
                return ToOutcome(first, flipped: false);
            }

            Logger.Info($"OAPA cal {axisLabel}: direction inconsistent, retrying with Reverse flipped ({reversed} -> {!reversed})");
            reportStatus?.Invoke($"{axisLabel}: auto-flipping Reverse and retrying...");

            var second = await CalibrateAxis(axis, currentRatio, !reversed, axisLabel, reportStatus, token).ConfigureAwait(false);
            if (second.Consistent) {
                Logger.Info($"OAPA cal {axisLabel}: auto-flip succeeded, ratio={second.Ratio:F2}");
                return ToOutcome(second, flipped: true);
            }

            Logger.Warning($"OAPA cal {axisLabel}: auto-flip did not resolve inconsistency; keeping original Reverse={reversed}");
            return ToOutcome(first, flipped: false);
        }

        private static AxisCalibrationOutcome ToOutcome(AxisCalibrationResult r, bool flipped) => new() {
            Ratio = r.Ratio,
            BacklashArcmin = r.BacklashArcmin,
            Consistent = r.Consistent,
            Asymmetric = r.Asymmetric,
            Flipped = flipped
        };

        private async Task<AxisCalibrationResult> CalibrateAxis(
            Axis axis, float currentRatio, bool reversed,
            string axisLabel, Action<string> reportStatus, CancellationToken token) {

            float commanded = calibrationStepArcmin;
            float step = reversed ? -commanded : commanded;
            bool isAzimuth = axis == Axis.XAxis;

            // Fail fast on an unsolvable field before commanding any motion.
            reportStatus?.Invoke($"{axisLabel}: baseline solve...");
            var baseline = await solver.CaptureAndSolve(token).ConfigureAwait(false);

            if (isAzimuth && Math.Cos(baseline.AltitudeDegrees * Math.PI / 180.0) < OapaCalibrationGeometry.MinimumAzimuthCosAltitude) {
                throw new InvalidOperationException(
                    $"{axisLabel}: field altitude {baseline.AltitudeDegrees:F0}\u00b0 is too close to the zenith for azimuth calibration. " +
                    "Point the scope at a lower altitude (ideally toward the celestial pole) and retry.");
            }

            float movedArcmin = 0f;
            try {
                reportStatus?.Invoke($"{axisLabel}: priming +{commanded:F0}'...");
                await motion.MoveRelative(axis, step, token).ConfigureAwait(false);
                movedArcmin += step;
                var solveA = await solver.CaptureAndSolve(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                reportStatus?.Invoke($"{axisLabel}: forward leg +{commanded:F0}'...");
                await motion.MoveRelative(axis, step, token).ConfigureAwait(false);
                movedArcmin += step;
                var solveB = await solver.CaptureAndSolve(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                reportStatus?.Invoke($"{axisLabel}: reversal leg -{commanded:F0}'...");
                await motion.MoveRelative(axis, -step, token).ConfigureAwait(false);
                movedArcmin -= step;
                var solveC = await solver.CaptureAndSolve(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                reportStatus?.Invoke($"{axisLabel}: reverse leg -{commanded:F0}'...");
                await motion.MoveRelative(axis, -step, token).ConfigureAwait(false);
                movedArcmin -= step;
                var solveD = await solver.CaptureAndSolve(token).ConfigureAwait(false);

                var forwardArcmin = OapaCalibrationGeometry.AxisDisplacementArcmin(isAzimuth, solveA, solveB);
                var reversalArcmin = OapaCalibrationGeometry.AxisDisplacementArcmin(isAzimuth, solveB, solveC);
                var reverseArcmin = OapaCalibrationGeometry.AxisDisplacementArcmin(isAzimuth, solveC, solveD);
                var directionConsistent = OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuth, solveA, solveB, commanded);

                var result = OapaCalibrationGeometry.ComputeAxisCalibration(
                    commanded, currentRatio, forwardArcmin, reversalArcmin, reverseArcmin, directionConsistent,
                    warning => Logger.Warning($"OAPA cal {axisLabel}: {warning}"));

                Logger.Info($"OAPA cal {axisLabel}: forward={forwardArcmin:F2}', reversal={reversalArcmin:F2}', reverse={reverseArcmin:F2}', " +
                    $"ratio={result.Ratio:F2}, backlash={result.BacklashArcmin:F2}', consistent={result.Consistent}, asymmetric={result.Asymmetric}");

                await CloseLoopAgainstBaseline(axis, isAzimuth, baseline, solveA, solveB, solveD, step, axisLabel, reportStatus, token).ConfigureAwait(false);
                return result;
            } catch (Exception) when (movedArcmin != 0f) {
                await BestEffortRestore(axis, isAzimuth, baseline, movedArcmin, axisLabel).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// A zero net command sum does not return the axis to its start: the reversal leg
        /// loses the backlash, leaving the axis roughly one backlash beyond the baseline.
        /// This measures the residual against the baseline solve and drives it back using
        /// the response the sequence itself just measured (signed forward displacement per
        /// wire arcminute), which works identically for normal and reversed axes. The
        /// closing move continues in the direction of the final legs, so it is itself
        /// backlash-free in the nominal case. One correction, one verification solve, no
        /// loops; the move is capped at one calibration step for pathological responses.
        /// </summary>
        private async Task CloseLoopAgainstBaseline(
            Axis axis, bool isAzimuth,
            CalibrationSolveSample baseline, CalibrationSolveSample solveA, CalibrationSolveSample solveB, CalibrationSolveSample solveD,
            float wireStep, string axisLabel, Action<string> reportStatus, CancellationToken token) {

            try {
                var residual = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, solveD);
                if (Math.Abs(residual) < RestoreToleranceArcmin) { return; }

                var responsePerWire = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, solveA, solveB) / wireStep;
                if (Math.Abs(responsePerWire) < 1e-3) { return; }

                var closing = (float)Math.Clamp(-residual / responsePerWire, -calibrationStepArcmin, calibrationStepArcmin);
                reportStatus?.Invoke($"{axisLabel}: returning to start ({residual:+0.0;-0.0}' off)...");
                await motion.MoveRelative(axis, closing, token).ConfigureAwait(false);

                var verify = await solver.CaptureAndSolve(token).ConfigureAwait(false);
                var finalResidual = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, verify);
                Logger.Info($"OAPA cal {axisLabel}: closed loop against baseline; residual {finalResidual:F2}' (was {residual:F2}')");
            } catch (Exception ex) {
                // The calibration result is already measured; a failed closing move must not discard it.
                Logger.Warning($"OAPA cal {axisLabel}: failed to close the loop against the baseline ({ex.Message})");
            }
        }

        /// <summary>
        /// Restore after a mid-sequence failure or cancellation. Driving back the commanded
        /// sum is blind to backlash, so this first tries to measure the true residual against
        /// the baseline solve and drive that back (assuming the current calibration is roughly
        /// right; capped for safety). Only when the solve itself is unavailable - often the
        /// reason the sequence failed - does it fall back to the commanded-sum restore.
        /// </summary>
        private async Task BestEffortRestore(Axis axis, bool isAzimuth, CalibrationSolveSample baseline, float movedArcmin, string axisLabel) {
            try {
                var current = await solver.CaptureAndSolve(CancellationToken.None).ConfigureAwait(false);
                var residual = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuth, baseline, current);
                var cap = 3f * calibrationStepArcmin;
                var restore = (float)Math.Clamp(-residual, -cap, cap);
                Logger.Info($"OAPA cal {axisLabel}: failure with {movedArcmin:F1}' commanded outstanding; measured {residual:F1}' from baseline, driving back");
                await motion.MoveRelative(axis, restore, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception measureEx) {
                Logger.Warning($"OAPA cal {axisLabel}: measured restore unavailable ({measureEx.Message}); driving back the commanded sum");
                try {
                    await motion.MoveRelative(axis, -movedArcmin, CancellationToken.None).ConfigureAwait(false);
                } catch (Exception restoreEx) {
                    Logger.Error($"OAPA cal {axisLabel}: failed to restore start position", restoreEx);
                }
            }
        }

        /// <summary>Residuals below this are indistinguishable from solve noise and left alone.</summary>
        private const float RestoreToleranceArcmin = 0.5f;
    }
}
