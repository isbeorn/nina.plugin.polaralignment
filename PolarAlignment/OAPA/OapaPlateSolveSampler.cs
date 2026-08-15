using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// Capture-and-solve boundary implementation for the OAPA self-calibration: captures a
    /// snapshot with the profile's plate-solve settings, solves it, and returns the solved
    /// field center with its topocentric altitude. Retries internally on solver failures.
    ///
    /// All samples of one calibration pass are transformed at a single reference epoch —
    /// the time of the pass's first solve. A tracked field keeps its RA/Dec while its
    /// Alt/Az drifts with sidereal time, so transforming each sample at its own wall-clock
    /// time would alias sky rotation into the displacement measurements: the direction
    /// check, the responses, and above all the closing moves against a minutes-old
    /// baseline (whose phantom residual no iteration can remove). Freezing the epoch makes
    /// every comparison mean "axis motion" and nothing else.
    /// </summary>
    public sealed class OapaPlateSolveSampler : IOapaCalibrationSolver {
        private const int PlateSolveRetries = 2;
        private const double SearchRadiusDeg = 30.0;

        private readonly IProfileService profileService;
        private readonly IImagingMediator imagingMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IPlateSolverFactory plateSolverFactory;
        private DateTime? sessionEpoch;

        public OapaPlateSolveSampler(
            IProfileService profileService,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IPlateSolverFactory plateSolverFactory) {
            this.profileService = profileService;
            this.imagingMediator = imagingMediator;
            this.telescopeMediator = telescopeMediator;
            this.plateSolverFactory = plateSolverFactory;
        }

        public async Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
            Exception lastException = null;
            for (int attempt = 0; attempt <= PlateSolveRetries; attempt++) {
                token.ThrowIfCancellationRequested();
                try {
                    var result = await CaptureAndSolveOnce(token).ConfigureAwait(false);
                    if (result != null && result.Success) {
                        return ToSample(result);
                    }
                    Logger.Warning($"Plate solve unsuccessful (attempt {attempt + 1}/{PlateSolveRetries + 1})");
                } catch (OperationCanceledException) { throw; } catch (Exception ex) {
                    lastException = ex;
                    Logger.Warning($"Plate solve attempt {attempt + 1} failed: {ex.Message}");
                }
            }
            throw new InvalidOperationException(
                $"Plate solve failed after {PlateSolveRetries + 1} attempts" +
                (lastException != null ? $": {lastException.Message}" : string.Empty));
        }

        /// <summary>Resets the reference epoch: the next solve of this pass will define it.</summary>
        public void BeginCalibration() {
            sessionEpoch = null;
        }

        private CalibrationSolveSample ToSample(PlateSolveResult solve) {
            var latitude = Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Latitude);
            var longitude = Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Longitude);
            var epoch = sessionEpoch ??= solve.Coordinates.DateTime.Now;
            return ToSample(solve.Coordinates, latitude, longitude, epoch);
        }

        /// <summary>
        /// Transforms a solved field center to the calibration sample frame at the given
        /// reference epoch. Kept as a seam so the epoch-freezing contract is testable:
        /// the same RA/Dec transformed at the same epoch must yield the same Alt/Az no
        /// matter when the solve actually happened.
        /// </summary>
        internal static CalibrationSolveSample ToSample(Coordinates coordinates, Angle latitude, Angle longitude, DateTime epoch) {
            var topocentric = coordinates.Transform(latitude, longitude, epoch);
            return new CalibrationSolveSample(coordinates.RADegrees, coordinates.Dec, topocentric.Altitude.Degree, topocentric.Azimuth.Degree);
        }

        private async Task<PlateSolveResult> CaptureAndSolveOnce(CancellationToken token) {
            var pss = profileService.ActiveProfile.PlateSolveSettings;
            var seq = new CaptureSequence() {
                Binning = new BinningMode(pss.Binning, pss.Binning),
                Gain = pss.Gain,
                ExposureTime = pss.ExposureTime,
                Offset = -1,
                FilterType = pss.Filter,
                ImageType = CaptureSequence.ImageTypes.SNAPSHOT
            };

            IRenderedImage image = await imagingMediator.CaptureAndPrepareImage(
                seq, new PrepareImageParameters(true, false), token, null);
            if (image == null) { return null; }

            var solver = plateSolverFactory.GetPlateSolver(pss);
            var imageSolver = plateSolverFactory.GetImageSolver(solver, null);
            var parameter = new PlateSolveParameter() {
                Binning = pss.Binning,
                Coordinates = telescopeMediator.GetCurrentPosition(),
                DownSampleFactor = pss.DownSampleFactor,
                FocalLength = profileService.ActiveProfile.TelescopeSettings.FocalLength,
                MaxObjects = pss.MaxObjects,
                PixelSize = profileService.ActiveProfile.CameraSettings.PixelSize,
                Regions = pss.Regions,
                SearchRadius = SearchRadiusDeg,
                DisableNotifications = true
            };
            return await imageSolver.Solve(image.RawImageData, parameter, null, token).ConfigureAwait(false);
        }
    }
}
