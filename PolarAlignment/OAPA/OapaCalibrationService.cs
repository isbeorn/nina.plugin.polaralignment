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
    /// pending backlash), solve A, +S, solve B, -S, solve C, -S, solve D. Net commanded motion
    /// is zero, so the axis ends where it started; on mid-sequence failure the accumulated
    /// commanded motion is driven back before rethrowing.
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
                var tangentDotNegative = OapaCalibrationGeometry.TangentDotProduct(solveA, solveB, solveB, solveC) < 0;

                var result = OapaCalibrationGeometry.ComputeAxisCalibration(
                    commanded, currentRatio, forwardArcmin, reversalArcmin, reverseArcmin, tangentDotNegative,
                    warning => Logger.Warning($"OAPA cal {axisLabel}: {warning}"));

                Logger.Info($"OAPA cal {axisLabel}: forward={forwardArcmin:F2}', reversal={reversalArcmin:F2}', reverse={reverseArcmin:F2}', " +
                    $"ratio={result.Ratio:F2}, backlash={result.BacklashArcmin:F2}', consistent={result.Consistent}, asymmetric={result.Asymmetric}");
                return result;
            } catch (Exception) when (movedArcmin != 0f) {
                // Best-effort: drive the axis back to its starting position before surfacing the error.
                Logger.Info($"OAPA cal {axisLabel}: failure with {movedArcmin:F1}' of commanded motion outstanding; driving back");
                try {
                    await motion.MoveRelative(axis, -movedArcmin, CancellationToken.None).ConfigureAwait(false);
                } catch (Exception restoreEx) {
                    Logger.Error($"OAPA cal {axisLabel}: failed to restore start position", restoreEx);
                }
                throw;
            }
        }
    }
}
