using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// The plugin's eyes for a calibration run without a polar alignment: it captures and
    /// solves one frame after another and forwards, as readings, how far each axis has moved
    /// the field since the first frame. The controller decides every move and computes the
    /// factors and the play from those readings, exactly as it does on TPPA's.
    ///
    /// The frames are sequential - the next is exposed after the previous reading was sent -
    /// which is the pattern the controller's one-reading skip after each move is built for.
    /// </summary>
    public static class OapaCalibrationFeed {

        private static readonly HashSet<string> RunningStates = new(StringComparer.Ordinal) {
            "preloading", "baseline", "probing", "measuring", "restoring"
        };

        /// <summary>
        /// The controller starts on the reading after the request, so by the second reading it
        /// reports a running state; the third is margin for a slow link. Still idle then, it has
        /// not taken the request (another source is driving it, or it simulates).
        /// </summary>
        private const int FramesToStart = 3;

        /// <summary>The controller's calibration is under way (a $K? state).</summary>
        public static bool IsRunning(string state) => state != null && RunningStates.Contains(state);

        /// <summary>
        /// Runs one calibration and returns the controller's final calibration line
        /// ("&lt;K|state:done|...|&gt;"). Stops after <paramref name="maxFrames"/> frames if the
        /// controller never finishes; throws when a frame cannot be solved, when the controller
        /// does not start, or on cancellation, and the caller stops the controller's calibration then.
        /// <paramref name="afterFrame"/> runs once each frame's reading has been sent and logged.
        /// </summary>
        public static async Task<string> Run(IOapaCalibrationSolver solver, IOapaAlignmentController controller,
                                             Action<string> onStatus, CancellationToken token, int maxFrames = 400,
                                             Action afterFrame = null) {
            solver.BeginCalibration();
            var reply = controller.RequestCalibrationOnly();
            Logger.Info($"OAPA calibration: calibrate-only requested -> {reply}");

            CalibrationSolveSample? reference = null;
            var started = false;
            for (var frame = 0; frame < maxFrames; frame++) {
                token.ThrowIfCancellationRequested();
                var sample = await solver.CaptureAndSolve(token).ConfigureAwait(false);
                reference ??= sample;

                var azimuth = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(true, reference.Value, sample);
                var altitude = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(false, reference.Value, sample);
                controller.ForwardError(azimuth, altitude);

                var calibration = controller.ControllerCalibrationStatus();
                var state = OapaControllerStatus.Parse(calibration, "K").TryGetValue("state", out var s) ? s : "";
                onStatus?.Invoke(OapaControllerStatus.Describe(controller.AlignmentStatus()));
                Logger.Info($"OAPA calibration: frame {frame + 1}, field moved AZ {azimuth:F2}' ALT {altitude:F2}'; {calibration}");
                afterFrame?.Invoke();

                if (IsRunning(state)) {
                    started = true;
                } else if (started) {
                    return calibration;
                } else if (frame + 1 >= FramesToStart) {
                    throw new InvalidOperationException($"the controller did not start the calibration ({calibration})");
                }
            }
            Logger.Warning($"OAPA calibration: the controller did not finish within {maxFrames} frames");
            return controller.ControllerCalibrationStatus();
        }
    }
}
