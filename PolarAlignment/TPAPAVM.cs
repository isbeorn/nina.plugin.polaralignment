using Accord.Math;
using Accord.Math.Geometry;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.Plugins.PolarAlignment.Avalon;
using NINA.Plugins.PolarAlignment.OAPA;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Behaviors;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NINA.Plugins.PolarAlignment {
    public class TPAPAVM : BaseINPC, IDisposable {

        public TPAPAVM(IProfileService profileService, IWeatherDataMediator weatherDataMediator)
            : this(profileService, weatherDataMediator, null, null) {
        }

        internal TPAPAVM(IProfileService profileService,
                         IWeatherDataMediator weatherDataMediator,
                         Func<IRenderedImage, Point, IProgress<ApplicationStatus>, CancellationToken, Task<Point>> referenceStarLocator,
                         TimeProvider timeProvider = null) {
            this.profileService = profileService;
            this.weatherDataMediator = weatherDataMediator;
            this.referenceStarLocator = referenceStarLocator ?? GetClosestStarPosition;
            this.timeProvider = timeProvider ?? TimeProvider.System;
            Status = new ApplicationStatus();

            Steps = new List<TPPAStep>() {
                new TPPAStep("Retrieving first measurement point"),
                new TPPAStep("Retrieving second measurement point"),
                new TPPAStep("Retrieving final measurement point"),
                new TPPAStep("Adjust Altitude / Azimuth")
            };

            DragMoveCommand = new RelayCommand(obj => {
                var dragResult = (DragResult)obj;
                if (dragResult.Mode == DragMode.Move) {
                    ErrorDetail.Shift(dragResult.Delta);
                }
            });
            LeftMouseButtonDownCommand = new AsyncCommand<bool>(async obj => { if (obj != null) { await SelectNewReferenceStar((Point)obj, CancellationToken.None); } return true; });
        }

        public UniversalPolarAlignmentVM UniversalPolarAlignmentVM => PolarAlignmentPlugin.UniversalPolarAlignmentVM;
        public UniversalPolarAlignmentOAPAVM UniversalPolarAlignmentOAPAVM => PolarAlignmentPlugin.UniversalPolarAlignmentOAPAVM;
        public IPolarAlignmentSystemVM ActiveAlignmentSystemVM => PolarAlignmentPlugin.ActiveAlignmentSystemVM;
        public bool UseContinuousErrorEstimator => Properties.Settings.Default.UseContinuousErrorEstimator;

        private readonly AutomatedAdjustmentController automatedAdjustmentController = new AutomatedAdjustmentController();
        private bool lastContinuousEstimateStable = true;

        public void ActivateFirstStep() {
            automatedAdjustmentController.Reset();
            lastContinuousEstimateStable = true;
            Steps[0].Active = true;
            Steps[0].Relevant = true;
        }
        public void ActivateSecondStep() {
            Steps[0].Active = false;
            Steps[0].Completed = true;

            Steps[1].Active = true;
            Steps[1].Relevant = true;
        }
        public void ActivateThirdStep() {
            Steps[1].Active = false;
            Steps[1].Completed = true;

            Steps[2].Active = true;
            Steps[2].Relevant = true;
        }
        public void ActivateFourthStep() {
            Steps[2].Active = false;
            Steps[2].Completed = true;

            Steps[0].Relevant = false;
            Steps[1].Relevant = false;
            Steps[2].Relevant = false;

            Steps[3].Active = true;
            Steps[3].Relevant = true;

            WaitingForUpdate = false;
        }



        private SemaphoreSlim selectNewStarLock = new SemaphoreSlim(1);
        private SemaphoreSlim starDetectionLock = new SemaphoreSlim(1);
        private static readonly TimeSpan ReferenceStarRedetectionInterval = TimeSpan.FromSeconds(120);
        private const double ReferenceStarRedetectionFieldShiftDegrees = 0.5;
        private readonly Func<IRenderedImage, Point, IProgress<ApplicationStatus>, CancellationToken, Task<Point>> referenceStarLocator;
        private readonly TimeProvider timeProvider;
        private DateTimeOffset? referenceStarLastDetectedAt;
        private Coordinates referenceStarDetectionField;

        public Task SelectNewReferenceStar(Point p, CancellationToken token) {
            return SetReferenceStar(p, true, token);
        }

        internal Task UseImageCenterAsReference(CancellationToken token) {
            return SetReferenceStar(Center, false, token);
        }

        private async Task SetReferenceStar(Point p, bool detectStar, CancellationToken token) {
            await selectNewStarLock.WaitAsync();
            try {
                if (detectStar) {
                    await DetectAndLockReferenceStar(PolarErrorDetermination.CurrentReferenceFrame,
                                                     p,
                                                     default,
                                                     token);
                } else {
                    ReferenceStar = p;
                    ReferenceStarCoordinates = null;
                    referenceStarLastDetectedAt = null;
                    referenceStarDetectionField = null;
                }

                await UpdateReferenceStarOverlay(token);
            } catch (Exception ex) {
                var action = detectStar ? "selection of new reference star" : "initialization of image-center reference";
                Logger.Error($"An error occurred during {action}", ex);
                Notification.ShowWarning(detectStar
                    ? "Failed to determine new reference star on current image"
                    : "Failed to initialize the correction overlay at the image center");
            } finally {
                selectNewStarLock.Release();
            }
        }

        internal async Task DetectAndLockReferenceStar(PlateSolveResult referenceFrame,
                                                       Point detectionReference,
                                                       IProgress<ApplicationStatus> progress,
                                                       CancellationToken token) {
            ReferenceStar = await referenceStarLocator(Image, detectionReference, progress, token);
            ReferenceStarCoordinates = referenceFrame.Coordinates.Shift(ReferenceStar.X - Center.X,
                                                                         ReferenceStar.Y - Center.Y,
                                                                         GetProjectionAngle(referenceFrame),
                                                                         ArcsecPerPix,
                                                                         ArcsecPerPix);
            referenceStarLastDetectedAt = timeProvider.GetUtcNow();
            referenceStarDetectionField = referenceFrame.Coordinates;
        }

        private async Task UpdateReferenceStarOverlay(CancellationToken token) {
            var refractionParams = RefractionParameters.GetRefractionParameters(weatherDataMediator.GetInfo());
            var useContinuousErrorEstimator = UseContinuousErrorEstimator;
            var overlay = await Task.Run(() => useContinuousErrorEstimator
                ? BuildErrorDetailComputation(PolarErrorDetermination,
                                              ReferenceStar,
                                              Center,
                                              ArcsecPerPix,
                                              refractionParams)
                : BuildLegacyErrorDetailComputation(PolarErrorDetermination,
                                                    ReferenceStar,
                                                    Center,
                                                    ArcsecPerPix,
                                                    refractionParams),
                                         token);
            ApplyErrorDetailComputation(overlay);
        }


        public async Task<bool> UpdateDetails(PlateSolveResult psr, IProgress<ApplicationStatus> progress, CancellationToken token) {
            PolarErrorDetermination.CurrentReferenceFrame = psr;
            var refractionParams = RefractionParameters.GetRefractionParameters(weatherDataMediator.GetInfo());
            PolarErrorDetermination.UpdateCurrentCorrectionFieldWarnings(refractionParams);
            var useContinuousErrorEstimator = UseContinuousErrorEstimator;
            var estimateStable = true;

            if (useContinuousErrorEstimator) {
                // The continuous estimator is pure math over the current plate solve and the last known state,
                // so it can be evaluated off the caller context without touching UI-bound properties.
                var estimate = await Task.Run(() => ContinuousPolarErrorEstimator.Estimate(PolarErrorDetermination,
                                                                                            psr,
                                                                                            refractionParams,
                                                                                            PolarErrorDetermination.CurrentMountAxisAzimuthError.Degree,
                                                                                            PolarErrorDetermination.CurrentMountAxisAltitudeError.Degree),
                                              token);

                if (estimate.Success) {
                    PolarErrorDetermination.CurrentMountAxisAzimuthError = Angle.ByDegree(estimate.AzimuthErrorDegrees);
                    PolarErrorDetermination.CurrentMountAxisAltitudeError = Angle.ByDegree(estimate.AltitudeErrorDegrees);
                    PolarErrorDetermination.CurrentMountAxisTotalError = Angle.ByDegree(Accord.Math.Tools.Hypotenuse(estimate.AzimuthErrorDegrees, estimate.AltitudeErrorDegrees));
                    automatedAdjustmentController.UpdateObservation(estimate.AzimuthErrorDegrees, estimate.AltitudeErrorDegrees);
                    lastContinuousEstimateStable = true;
                } else {
                    Logger.Warning($"Continuous polar error estimate was unstable. Condition number: {estimate.ConditionNumber}; residual: {estimate.ResidualArcSeconds}\"");
                    lastContinuousEstimateStable = false;
                    estimateStable = false;
                }
            } else {
                lastContinuousEstimateStable = true;
            }

            await UpdateReferenceStar(psr, progress, token);

            var overlay = await Task.Run(() => useContinuousErrorEstimator
                ? BuildErrorDetailComputation(PolarErrorDetermination,
                                              ReferenceStar,
                                              Center,
                                              ArcsecPerPix,
                                              refractionParams)
                : BuildLegacyErrorDetailComputation(PolarErrorDetermination,
                                                    ReferenceStar,
                                                    Center,
                                                    ArcsecPerPix,
                                                    refractionParams),
                                         token);
            ApplyErrorDetailComputation(overlay);
            WaitingForUpdate = false;
            return estimateStable;
        }

        internal async Task UpdateReferenceStar(PlateSolveResult psr, IProgress<ApplicationStatus> progress, CancellationToken token) {
            await selectNewStarLock.WaitAsync(token);
            try {
                var referenceStarCoordinates = ReferenceStarCoordinates;
                if (referenceStarCoordinates == null) {
                    ReferenceStar = Center;
                    return;
                }

                var projectedReferenceStar = await Task.Run(() => referenceStarCoordinates.XYProjection(psr.Coordinates,
                                                                                                        Center,
                                                                                                        ArcsecPerPix,
                                                                                                        ArcsecPerPix,
                                                                                                        GetProjectionAngle(psr)),
                                                            token);
                var outsideImage = ReferenceStarOutsideImage(Image, projectedReferenceStar);
                var elapsed = referenceStarLastDetectedAt.HasValue
                    ? timeProvider.GetUtcNow() - referenceStarLastDetectedAt.Value
                    : TimeSpan.MaxValue;
                var fieldShiftDegrees = referenceStarDetectionField == null
                    ? double.PositiveInfinity
                    : (psr.Coordinates - referenceStarDetectionField).Distance.Degree;
                var redetectionRequired = outsideImage
                    || elapsed >= ReferenceStarRedetectionInterval
                    || fieldShiftDegrees > ReferenceStarRedetectionFieldShiftDegrees;

                if (redetectionRequired) {
                    await DetectAndLockReferenceStar(psr,
                                                     outsideImage ? Center : projectedReferenceStar,
                                                     progress,
                                                     token);
                } else {
                    ReferenceStar = projectedReferenceStar;
                }
            } finally {
                selectNewStarLock.Release();
            }
        }

        private static bool ReferenceStarOutsideImage(IRenderedImage image, Point referenceStar) {
            if (!double.IsFinite(referenceStar.X) || !double.IsFinite(referenceStar.Y)) { return true; }

            var renderedImage = image?.Image;
            if (renderedImage == null) { return false; }

            return referenceStar.X < 0
                || referenceStar.X >= renderedImage.PixelWidth
                || referenceStar.Y < 0
                || referenceStar.Y >= renderedImage.PixelHeight;
        }

        public async Task MoveCloser(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var activeSystem = ActiveAlignmentSystemVM;
            if (activeSystem == null || !activeSystem.DoAutomatedAdjustments) { return; }

            var useContinuousErrorEstimator = UseContinuousErrorEstimator;

            if (useContinuousErrorEstimator && !lastContinuousEstimateStable) {
                progress?.Report(new ApplicationStatus() { Status = "Skipping automated adjustment because the continuous error estimate is unstable." });
                return;
            }

            if (useContinuousErrorEstimator && PolarErrorDetermination.CurrentCorrectionFieldNearEastWest) {
                progress?.Report(new ApplicationStatus() { Status = "Skipping automated adjustment because the current correction field is too close to exact east or west." });
                return;
            }

            // Per-cycle move cap auto-scales with the current measured error so multi-degree
            // initial errors converge in a handful of cycles without user intervention, while the
            // final approach stays gentle. The "Max correction per cycle" setting acts as a
            // safety ceiling on top of the auto-scaled value.
            var currentTotalErrorArcmin = Math.Abs(PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes);
            var autoCap = Math.Max(AutomatedAdjustmentController.DefaultMaximumMoveMagnitude, currentTotalErrorArcmin * 0.8);
            automatedAdjustmentController.MaximumMoveMagnitude = Math.Min(autoCap, Properties.Settings.Default.MaxAutomatedCorrectionMagnitude);

            var plan = automatedAdjustmentController.CreatePlan();
            if (!plan.HasMovement) {
                progress?.Report(new ApplicationStatus() { Status = plan.Reason });
                return;
            }

            progress?.Report(new ApplicationStatus() {
                Status = $"{plan.Reason}: X {Math.Round(plan.XMagnitude, 2)}, Y {Math.Round(plan.YMagnitude, 2)}"
            });

            var executedX = 0.0;
            var executedY = 0.0;

            if (Math.Abs(plan.XMagnitude) > 0) {
                if (!await activeSystem.TryNudgeX((float)plan.XMagnitude, token)) {
                    automatedAdjustmentController.NoteFailedExecution();
                    return;
                }
                executedX = plan.XMagnitude;
            }

            if (Math.Abs(plan.YMagnitude) > 0) {
                if (!await activeSystem.TryNudgeY((float)plan.YMagnitude, token)) {
                    if (Math.Abs(executedX) > 0) {
                        automatedAdjustmentController.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(executedX,
                                                                                                           0,
                                                                                                           plan.IsProbe,
                                                                                                           $"{plan.Reason} (partial X move)"));
                        await CoreUtil.Wait(TimeSpan.FromSeconds(activeSystem.AutomatedAdjustmentSettleTime), token, progress, "Settling");
                        return;
                    }

                    automatedAdjustmentController.NoteFailedExecution();
                    return;
                }
                executedY = plan.YMagnitude;
            }

            automatedAdjustmentController.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(executedX, executedY, plan.IsProbe, plan.Reason));
            await CoreUtil.Wait(TimeSpan.FromSeconds(activeSystem.AutomatedAdjustmentSettleTime), token, progress, "Settling");
        }

        internal sealed class ErrorDetailComputation {
            public ErrorDetailComputation(ErrorDetail current, ErrorDetail initial, double? azimuthErrorDegrees = null, double? altitudeErrorDegrees = null) {
                Current = current;
                Initial = initial;
                AzimuthErrorDegrees = azimuthErrorDegrees;
                AltitudeErrorDegrees = altitudeErrorDegrees;
            }

            public ErrorDetail Current { get; }
            public ErrorDetail Initial { get; }
            public double? AzimuthErrorDegrees { get; }
            public double? AltitudeErrorDegrees { get; }
            public bool HasErrorEstimate => AzimuthErrorDegrees.HasValue && AltitudeErrorDegrees.HasValue;
        }

        internal ErrorDetailComputation BuildLegacyErrorDetailComputation(PolarErrorDetermination determination,
                                                                          Point referenceStar,
                                                                          Point center,
                                                                          double arcsecPerPix,
                                                                          RefractionParameters refractionParams) {
            var currentCenter = determination.CurrentReferenceFrame;
            var originPixel = determination.InitialReferenceFrame.Coordinates.XYProjection(currentCenter.Coordinates,
                                                                                           center,
                                                                                           arcsecPerPix,
                                                                                           arcsecPerPix,
                                                                                           GetProjectionAngle(currentCenter));

            var pointShift = center - originPixel;
            originPixel = originPixel + pointShift * 2;

            var destinationAltAz = determination.GetDestinationCoordinates(-determination.InitialMountAxisAzimuthError.Degree,
                                                                           -determination.InitialMountAxisAltitudeError.Degree,
                                                                           refractionParams).Transform(Epoch.J2000);
            var destinationPixel = destinationAltAz.XYProjection(currentCenter.Coordinates,
                                                                 center,
                                                                 arcsecPerPix,
                                                                 arcsecPerPix,
                                                                 GetProjectionAngle(currentCenter));
            destinationPixel = destinationPixel + pointShift * 2;

            var originalAzimuthAltAz = determination.GetDestinationCoordinates(-determination.InitialMountAxisAzimuthError.Degree,
                                                                               0,
                                                                               refractionParams).Transform(Epoch.J2000);
            var originalAzimuthPixel = originalAzimuthAltAz.XYProjection(currentCenter.Coordinates,
                                                                         center,
                                                                         arcsecPerPix,
                                                                         arcsecPerPix,
                                                                         GetProjectionAngle(currentCenter));
            originalAzimuthPixel = originalAzimuthPixel + pointShift * 2;

            var lineOriginToAzimuth = Line.FromPoints(ToAccordPoint(originPixel), ToAccordPoint(originalAzimuthPixel));
            var correctedAzimuthLine = Line.FromSlopeIntercept(lineOriginToAzimuth.Slope, (float)(center.Y - lineOriginToAzimuth.Slope * center.X));
            var lineAzimuthToDestination = Line.FromPoints(ToAccordPoint(originalAzimuthPixel), ToAccordPoint(destinationPixel));
            var correctedAzimuthPixel = lineAzimuthToDestination.GetIntersectionWith(correctedAzimuthLine);

            var correctedAzimuthDistance = correctedAzimuthPixel.Value.DistanceTo(ToAccordPoint(center));
            var originalAzimuthDistance = ToAccordPoint(originalAzimuthPixel).DistanceTo(ToAccordPoint(originPixel));

            var originalAltitudeAltAz = determination.GetDestinationCoordinates(0,
                                                                                -determination.InitialMountAxisAltitudeError.Degree,
                                                                                refractionParams).Transform(Epoch.J2000);
            var originalAltitudePixel = originalAltitudeAltAz.XYProjection(currentCenter.Coordinates,
                                                                           center,
                                                                           arcsecPerPix,
                                                                           arcsecPerPix,
                                                                           GetProjectionAngle(currentCenter));
            originalAltitudePixel = originalAltitudePixel + pointShift * 2;

            var lineOriginToAltitude = Line.FromPoints(ToAccordPoint(originPixel), ToAccordPoint(originalAltitudePixel));
            var correctedAltitudeLine = Line.FromSlopeIntercept(lineOriginToAltitude.Slope, (float)(center.Y - lineOriginToAltitude.Slope * center.X));
            var lineAltitudeToDestination = Line.FromPoints(ToAccordPoint(originalAltitudePixel), ToAccordPoint(destinationPixel));
            var correctedAltitudePixel = lineAltitudeToDestination.GetIntersectionWith(correctedAltitudeLine);

            var correctedAltitudeDistance = correctedAltitudePixel.Value.DistanceTo(ToAccordPoint(center));
            var originalAltitudeDistance = ToAccordPoint(originalAltitudePixel).DistanceTo(ToAccordPoint(originPixel));

            var originalAzimuthDirection = ToAccordPoint(destinationPixel) - ToAccordPoint(originalAltitudePixel);
            var correctedAzimuthDirection = ToAccordPoint(destinationPixel) - correctedAltitudePixel.Value;
            var azimuthSameDirection = (originalAzimuthDirection.X * correctedAzimuthDirection.X + originalAzimuthDirection.Y * correctedAzimuthDirection.Y) > 0;
            var azSign = azimuthSameDirection ? 1 : -1;

            var originalAltitudeDirection = ToAccordPoint(destinationPixel) - ToAccordPoint(originalAzimuthPixel);
            var correctedAltitudeDirection = ToAccordPoint(destinationPixel) - correctedAzimuthPixel.Value;
            var altitudeSameDirection = (originalAltitudeDirection.X * correctedAltitudeDirection.X + originalAltitudeDirection.Y * correctedAltitudeDirection.Y) > 0;
            var altSign = altitudeSameDirection ? 1 : -1;

            var azimuthErrorDegrees = determination.InitialMountAxisAzimuthError.Degree * (azSign * correctedAzimuthDistance / originalAzimuthDistance);
            var altitudeErrorDegrees = determination.InitialMountAxisAltitudeError.Degree * (altSign * correctedAltitudeDistance / originalAltitudeDistance);

            var shift = referenceStar - center;
            var current = new ErrorDetail(center,
                                          ToPoint(correctedAltitudePixel.Value),
                                          ToPoint(correctedAzimuthPixel.Value),
                                          destinationPixel);
            current.Shift(shift);

            var initial = new ErrorDetail(originPixel,
                                          originalAltitudePixel,
                                          originalAzimuthPixel,
                                          destinationPixel);
            initial.Shift(shift);

            return new ErrorDetailComputation(current,
                                              initial,
                                              azimuthErrorDegrees,
                                              altitudeErrorDegrees);
        }

        internal ErrorDetailComputation BuildErrorDetailComputation(PolarErrorDetermination determination,
                                                                    Point referenceStar,
                                                                    Point center,
                                                                    double arcsecPerPix,
                                                                    RefractionParameters refractionParams) {
            var currentCenter = determination.CurrentReferenceFrame;
            var originPixel = determination.InitialReferenceFrame.Coordinates.XYProjection(currentCenter.Coordinates,
                                                                                           center,
                                                                                           arcsecPerPix,
                                                                                           arcsecPerPix,
                                                                                           GetProjectionAngle(currentCenter));

            var pointShift = center - originPixel;
            originPixel = originPixel + pointShift * 2;

            Point ProjectDestination(double azimuthAngleDegrees, double altitudeAngleDegrees) {
                var coordinates = determination.GetDestinationCoordinates(azimuthAngleDegrees,
                                                                          altitudeAngleDegrees,
                                                                          refractionParams)
                    .Transform(Epoch.J2000);

                return coordinates.XYProjection(currentCenter.Coordinates,
                                                center,
                                                arcsecPerPix,
                                                arcsecPerPix,
                                                GetProjectionAngle(currentCenter)) + pointShift * 2;
            }

            var destinationPixel = ProjectDestination(-determination.InitialMountAxisAzimuthError.Degree,
                                                      -determination.InitialMountAxisAltitudeError.Degree);

            var originalAzimuthPixel = ProjectDestination(-determination.InitialMountAxisAzimuthError.Degree, 0);
            var correctedAzimuthPixel = Intersect(center,
                                                  originalAzimuthPixel - originPixel,
                                                  originalAzimuthPixel,
                                                  destinationPixel - originalAzimuthPixel);

            var originalAltitudePixel = ProjectDestination(0, -determination.InitialMountAxisAltitudeError.Degree);
            var correctedAltitudePixel = Intersect(center,
                                                   originalAltitudePixel - originPixel,
                                                   originalAltitudePixel,
                                                   destinationPixel - originalAltitudePixel);

            var shift = referenceStar - center;
            var current = new ErrorDetail(center,
                                          correctedAltitudePixel,
                                          correctedAzimuthPixel,
                                          destinationPixel);
            current.Shift(shift);

            var initial = new ErrorDetail(originPixel,
                                          originalAltitudePixel,
                                          originalAzimuthPixel,
                                          destinationPixel);
            initial.Shift(shift);

            return new ErrorDetailComputation(
                current,
                initial
            );
        }

        private static Point Intersect(Point firstPoint, System.Windows.Vector firstDirection, Point secondPoint, System.Windows.Vector secondDirection) {
            var cross = Cross(firstDirection, secondDirection);
            if (Math.Abs(cross) < 1e-12) {
                throw new InvalidOperationException("Unable to build polar-alignment overlay because correction component lines are parallel.");
            }

            var distance = secondPoint - firstPoint;
            var t = Cross(distance, secondDirection) / cross;
            return firstPoint + firstDirection * t;
        }

        private static double Cross(System.Windows.Vector first, System.Windows.Vector second) {
            return first.X * second.Y - first.Y * second.X;
        }

        public static Accord.Point ToAccordPoint(Point p) {
            return new Accord.Point((float)p.X, (float)p.Y);
        }

        public static Point ToPoint(Accord.Point p) {
            return new Point(p.X, p.Y);
        }

#pragma warning disable CS0618 // The legacy overlay/reacquisition projection was authored against Orientation.
        private static double GetProjectionAngle(PlateSolveResult plateSolveResult) => plateSolveResult.Orientation;
#pragma warning restore CS0618

        private void ApplyErrorDetailComputation(ErrorDetailComputation overlay) {
            if (overlay.HasErrorEstimate) {
                var azimuthErrorDegrees = overlay.AzimuthErrorDegrees.Value;
                var altitudeErrorDegrees = overlay.AltitudeErrorDegrees.Value;
                PolarErrorDetermination.CurrentMountAxisAzimuthError = Angle.ByDegree(azimuthErrorDegrees);
                PolarErrorDetermination.CurrentMountAxisAltitudeError = Angle.ByDegree(altitudeErrorDegrees);
                PolarErrorDetermination.CurrentMountAxisTotalError = Angle.ByDegree(Accord.Math.Tools.Hypotenuse(altitudeErrorDegrees, azimuthErrorDegrees));
                automatedAdjustmentController.UpdateObservation(azimuthErrorDegrees, altitudeErrorDegrees);
                lastContinuousEstimateStable = true;
            }

            ErrorDetail = overlay.Current;
            ErrorDetail2 = overlay.Initial;

            if (Properties.Settings.Default.LogError) {
                if (logger == null) {
                    CreateLogger();
                }
                logger.Information("{@Wrapper}",
                    new {
                        Longitude = PolarErrorDetermination.Longitude.Degree,
                        Latitude = PolarErrorDetermination.Latitude.Degree,
                        AltitudeError = PolarErrorDetermination.CurrentMountAxisAltitudeError.Degree,
                        AzimuthError = PolarErrorDetermination.CurrentMountAxisAzimuthError.Degree,
                        TotalError = PolarErrorDetermination.CurrentMountAxisTotalError.Degree
                    }
                );
            }
        }

        private void CreateLogger() {
            var logDate = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var ninadocs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A");
            if (!Directory.Exists(ninadocs)) { Directory.CreateDirectory(ninadocs); }

            var logDir = Path.Combine(ninadocs, "PolarAlignment");
            if (!Directory.Exists(logDir)) { Directory.CreateDirectory(logDir); }

            var logFilePath = Path.Combine(logDir, $"{logDate}-PolarAlignment.log");
            this.logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
              .WriteTo.File(logFilePath,
                    rollingInterval: RollingInterval.Infinite,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} - {Message:lj}{NewLine}",
                    shared: true,
                    buffered: false,
                    retainedFileCountLimit: null,
                    flushToDiskInterval: TimeSpan.FromSeconds(1)
                )
              .CreateLogger();
        }

        public async Task<Point> GetClosestStarPosition(IRenderedImage image, Point reference, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var detection = await GetStarDetection(image, progress, token);
            token.ThrowIfCancellationRequested();
            var stars = detection?.StarList;
            if (stars == null) {
                Logger.Warning("Star detection did not return a star list. Using previous reference point instead");
                return reference;
            }

            var closestStarToReference = stars
                .GroupBy(p => Math.Pow(reference.X - p.Position.X, 2) + Math.Pow(reference.Y - p.Position.Y, 2))
                .OrderBy(p => p.Key)
                .FirstOrDefault()?.FirstOrDefault();
            if (closestStarToReference == null) {
                Logger.Warning("No star could be found. Using previous reference point instead");
                return reference;
            }
            return new Point(closestStarToReference.Position.X, closestStarToReference.Position.Y);
        }


        private KeyValuePair<int, StarDetectionResult> starDetection;
        private async Task<StarDetectionResult> GetStarDetection(IRenderedImage image, IProgress<ApplicationStatus> progress, CancellationToken token) {
            await starDetectionLock.WaitAsync();
            try {
                if (starDetection.Value == null || starDetection.Key != image.RawImageData.MetaData.Image.Id) {
                    var detection = new StarDetection();

                    var detectionParams = new StarDetectionParams() {
                        Sensitivity = profileService.ActiveProfile.ImageSettings.StarSensitivity,
                        NoiseReduction = profileService.ActiveProfile.ImageSettings.NoiseReduction
                    };

                    // Reference-star selection and locked-mode reacquisition can still be heavy image-analysis steps.
                    // Some star-detection paths do noticeable synchronous work before yielding, so
                    // invoke the detector from a background task and only marshal the finished result back.
                    var detectionResult = await Task.Run(async () => await detection.Detect(image,
                                                                                             image.Image.Format,
                                                                                             detectionParams,
                                                                                             progress,
                                                                                             token).ConfigureAwait(false),
                                                       token);

                    starDetection = new KeyValuePair<int, StarDetectionResult>(image.RawImageData.MetaData.Image.Id, detectionResult);
                }
                return starDetection.Value;
            } finally {
                starDetectionLock.Release();
            }
        }

        public void Dispose() {
            try {
                logger?.Dispose();
            } catch { }
            try {
                ActiveAlignmentSystemVM?.Disconnect();
            } catch { }
        }

        private PolarErrorDetermination polarErrorDetermination;
        public PolarErrorDetermination PolarErrorDetermination {
            get => polarErrorDetermination;
            set {
                polarErrorDetermination = value;
                RaisePropertyChanged();
            }
        }

        public ICommand DragMoveCommand { get; }
        public ICommand LeftMouseButtonDownCommand { get; }

        private Serilog.Core.Logger logger;

        public List<TPPAStep> Steps { get; }

        private ApplicationStatus status;
        private IProfileService profileService;
        private IWeatherDataMediator weatherDataMediator;

        public ApplicationStatus Status { get => status; set { status = value; RaisePropertyChanged(); } }

        public bool Northern {
            get => Latitude.Degree > 0;
        }

        private IRenderedImage image;

        private ErrorDetail erorLines;
        public ErrorDetail ErrorDetail { get => erorLines; set { erorLines = value; RaisePropertyChanged(); } }

        private ErrorDetail erorLines2;
        public ErrorDetail ErrorDetail2 { get => erorLines2; set { erorLines2 = value; RaisePropertyChanged(); } }
        public Angle Latitude {
            get => Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Latitude);
        }
        public Angle Longitude {
            get => Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Longitude);
        }
        public IRenderedImage Image {
            get => image;
            internal set {
                image = value;
                WaitingForUpdate = true;
                RaisePropertyChanged();
            }
        }

        public Point ReferenceStar { get; private set; }
        public Coordinates ReferenceStarCoordinates { get; internal set; }
        public Point Center { get; internal set; }
        private double arcsecPerPix;
        public double ArcsecPerPix {
            get => arcsecPerPix;
            internal set {
                arcsecPerPix = value;
                RaisePropertyChanged();
            }
        }

        private bool waitingForUpdate;
        public bool WaitingForUpdate {
            get => waitingForUpdate;
            private set {


                waitingForUpdate = value;
                RaisePropertyChanged();
            }
        }
    }

    public class ErrorDetail : BaseINPC {

        public ErrorDetail(Point origin, Point altitude, Point azimuth, Point total) {
            Origin = origin;
            Altitude = altitude;
            Azimuth = azimuth;
            Total = total;
            RaisePropertyChanged(nameof(Rectangle));
        }

        public Point Origin { get; set; }
        public Point Altitude { get; set; }
        public Point Azimuth { get; set; }
        public Point Total { get; set; }

        public PointCollection Rectangle {
            get => new PointCollection  {
                Origin,
                Altitude,
                Total,
                Azimuth
             };
        }



        public void Shift(System.Windows.Vector delta) {
            Origin = new Point(Origin.X + delta.X, Origin.Y + delta.Y);
            Altitude = new Point(Altitude.X + delta.X, Altitude.Y + delta.Y);
            Azimuth = new Point(Azimuth.X + delta.X, Azimuth.Y + delta.Y);
            Total = new Point(Total.X + delta.X, Total.Y + delta.Y);
            RaiseAllPropertiesChanged();
        }
    }

    public class ErrorLines : BaseINPC {


        private ErrorLine total;
        public ErrorLine Total { get => total; set { total = value; RaisePropertyChanged(); } }


        private ErrorLine altitude;
        public ErrorLine Altitude { get => altitude; set { altitude = value; RaisePropertyChanged(); } }


        private ErrorLine azimuth;
        public ErrorLine Azimuth { get => azimuth; set { azimuth = value; RaisePropertyChanged(); } }


        private Point delta;
        public Point Delta { get => delta; set { delta = value; RaisePropertyChanged(); } }
    }

    public class ErrorLine {
        public ErrorLine(Point start, Point end) {
            Start = start;
            End = end;
        }

        public Point Start { get; }
        public Point End { get; }
    }


    public class PolarErrorDetermination : BaseINPC {
        private const double EastWestWarningThresholdDegrees = 5.0;

        public PolarErrorDetermination(PlateSolveResult referenceFrame, Position position1, Position position2, Position position3, Angle latitude, Angle longitude, double elevation, RefractionParameters refractionParameters, bool correctForRefraction, double declinationSpreadArcsec = 0) {
            Latitude = latitude;
            Longitude = longitude;
            Elevation = elevation;

            InitialReferenceFrame = referenceFrame;
            FirstPosition = position1;
            SecondPosition = position2;
            ThirdPosition = position3;

            DeclinationSpreadArcsec = declinationSpreadArcsec;

            var planeVector = Vector3.DeterminePlaneVector(FirstPosition.Vector, SecondPosition.Vector, ThirdPosition.Vector);

            if ((Northern && planeVector.X < 0) || (!Northern && planeVector.X > 0)) {
                // Flip vector if pointing to the wrong direction
                planeVector = new Vector3(-planeVector.X, -planeVector.Y, -planeVector.Z);
            }


            InitialMountAxisErrorPosition = new Position(planeVector, Latitude, Longitude, Elevation);

            CalculateMountAxisError(refractionParameters, correctForRefraction);
            UpdateCurrentCorrectionFieldWarnings(refractionParameters);
        }

        public Angle Latitude { get; }
        public Angle Longitude { get; }
        public double Elevation { get; }
        public bool Northern {
            get => Latitude.Degree > 0;
        }

        public PlateSolveResult InitialReferenceFrame { get; }
        public Position FirstPosition { get; }
        public Position SecondPosition { get; }
        public Position ThirdPosition { get; }

        public Position InitialMountAxisErrorPosition { get; }

        [JsonProperty]
        public Angle InitialMountAxisAltitudeError { get; private set; }
        [JsonProperty]
        public Angle InitialMountAxisAzimuthError { get; private set; }
        [JsonProperty]
        public Angle InitialMountAxisTotalError { get; private set; }
        public PlateSolveResult CurrentReferenceFrame { get; set; }

        private Angle currentMountAxisAltitudeError;
        public Angle CurrentMountAxisAltitudeError {
            get => currentMountAxisAltitudeError;
            set {
                currentMountAxisAltitudeError = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CurrentMountAxisAltitudeErrorDirection));
            }
        }
        private Angle currentMountAxisAzimuthError;
        public Angle CurrentMountAxisAzimuthError {
            get => currentMountAxisAzimuthError;
            set {
                currentMountAxisAzimuthError = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CurrentMountAxisAzimuthErrorDirection));
            }
        }
        private Angle currentMountAxisTotalError;
        public Angle CurrentMountAxisTotalError {
            get => currentMountAxisTotalError;
            set {
                currentMountAxisTotalError = value;
                RaisePropertyChanged();
            }
        }
        public double DeclinationSpreadArcsec { get; init; }

        public bool DeclinationSpreadLarge {
            get => DeclinationSpreadArcsec > 2;
        }

        public bool InitialErrorLarge {
            get => InitialMountAxisTotalError.Degree > 2 && InitialMountAxisTotalError.Degree <= 10;
        }

        public bool InitialErrorHuge {
            get => InitialMountAxisTotalError.Degree > 10;
        }

        private double currentCorrectionFieldAzimuthDegrees;
        public double CurrentCorrectionFieldAzimuthDegrees {
            get => currentCorrectionFieldAzimuthDegrees;
            private set {
                currentCorrectionFieldAzimuthDegrees = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CurrentCorrectionFieldDistanceToEastWestDegrees));
                RaisePropertyChanged(nameof(CurrentCorrectionFieldNearEastWest));
            }
        }

        private double currentCorrectionFieldAltitudeDegrees;
        public double CurrentCorrectionFieldAltitudeDegrees {
            get => currentCorrectionFieldAltitudeDegrees;
            private set {
                currentCorrectionFieldAltitudeDegrees = value;
                RaisePropertyChanged();
            }
        }

        public double CurrentCorrectionFieldDistanceToEastWestDegrees {
            get {
                var distanceToEast = Math.Abs(NormalizeSignedDegrees(CurrentCorrectionFieldAzimuthDegrees - 90.0));
                var distanceToWest = Math.Abs(NormalizeSignedDegrees(CurrentCorrectionFieldAzimuthDegrees - 270.0));
                return Math.Min(distanceToEast, distanceToWest);
            }
        }

        public bool CurrentCorrectionFieldNearEastWest {
            get => CurrentReferenceFrame?.Coordinates != null && CurrentCorrectionFieldDistanceToEastWestDegrees <= EastWestWarningThresholdDegrees;
        }

        public string CurrentMountAxisAltitudeErrorDirection {
            get {
                if (CurrentMountAxisAltitudeError.Degree > 0) {
                    if (Northern) {
                        return "🠗 Move down";
                    } else {
                        return "Move up 🠕";
                    }
                } else if (CurrentMountAxisAltitudeError.Degree < 0) {
                    if (Northern) {
                        return "Move up 🠕";
                    } else {
                        return "🠗 Move down";
                    }
                } else {
                    return string.Empty;
                }
            }
        }
        public string CurrentMountAxisAzimuthErrorDirection {
            get {
                if (CurrentMountAxisAzimuthError.Degree > 0) {
                    if (Northern) {
                        return "🠔 Move left/west";
                    } else {
                        return "🠔 Move left/east";
                    }
                } else if (CurrentMountAxisAzimuthError.Degree < 0) {
                    if (Northern) {
                        return "Move right/east 🠖";
                    } else {
                        return "Move right/west 🠖";
                    }
                } else {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Calculate the error based on the measured telescope axis compared to the polar axis
        /// Polar axis = Azimuth 0 | Altitude = Latitude
        /// </summary>
        /// <returns></returns>
        private void CalculateMountAxisError(RefractionParameters refractionParameters, bool correctForRefraction) {
            var altitudeError = Angle.Zero;
            var azimuthError = Angle.Zero;

            var pole = Math.Abs(Latitude.Degree);
            if (!correctForRefraction) {
                // When refraction is disabled, adjust the calculation of the true pole to the refracted pole
                pole = AstroUtil.CalculateRefractedAltitude(pole, refractionParameters.PressureHPa, refractionParameters.Temperature, refractionParameters.RelativeHumidity, refractionParameters.Wavelength);
                if(double.IsNaN(pole)) {
                    pole = Math.Abs(Latitude.Degree);
                    Logger.Error($"Refracted pole could not be calculated. Pressure: {refractionParameters.PressureHPa}, Temperature: {refractionParameters.Temperature}, Humidity: {refractionParameters.RelativeHumidity}, Wavelength: {refractionParameters.Wavelength}. Falling back to non-refracted pole: {pole}");
                }
            }

            if (Northern) {
                altitudeError = Angle.ByDegree(InitialMountAxisErrorPosition.Topocentric.Altitude.Degree - pole);
                azimuthError = InitialMountAxisErrorPosition.Topocentric.Azimuth;
            } else {
                altitudeError = Angle.ByDegree(pole - InitialMountAxisErrorPosition.Topocentric.Altitude.Degree);
                azimuthError = InitialMountAxisErrorPosition.Topocentric.Azimuth + Angle.ByDegree(180);
            }

            if (azimuthError.Degree > 180) {
                azimuthError = Angle.ByDegree(azimuthError.Degree - 360);
            }
            if (azimuthError.Degree < -180) {
                azimuthError = Angle.ByDegree(azimuthError.Degree + 360);
            }

            InitialMountAxisAltitudeError = altitudeError;
            InitialMountAxisAzimuthError = azimuthError;
            InitialMountAxisTotalError = Angle.ByDegree(Accord.Math.Tools.Hypotenuse(InitialMountAxisAltitudeError.Degree, InitialMountAxisAzimuthError.Degree));

            CurrentMountAxisAltitudeError = altitudeError;
            CurrentMountAxisAzimuthError = azimuthError;
            CurrentMountAxisTotalError = Angle.ByDegree(Accord.Math.Tools.Hypotenuse(InitialMountAxisAltitudeError.Degree, InitialMountAxisAzimuthError.Degree));
            CurrentReferenceFrame = InitialReferenceFrame;
        }

        internal void UpdateCurrentCorrectionFieldWarnings(RefractionParameters refractionParameters) {
            if (CurrentReferenceFrame?.Coordinates == null) {
                CurrentCorrectionFieldAzimuthDegrees = double.NaN;
                CurrentCorrectionFieldAltitudeDegrees = double.NaN;
                return;
            }

            refractionParameters = refractionParameters ?? RefractionParameters.GetRefractionParameters();
            var observationTime = CurrentReferenceFrame.Coordinates.DateTime.Now;
            var topocentric = CurrentReferenceFrame.Coordinates.Transform(Latitude,
                                                                         Longitude,
                                                                         Elevation,
                                                                         refractionParameters.PressureHPa,
                                                                         refractionParameters.Temperature,
                                                                         refractionParameters.RelativeHumidity,
                                                                         refractionParameters.Wavelength,
                                                                         observationTime);

            CurrentCorrectionFieldAzimuthDegrees = topocentric.Azimuth.Degree;
            CurrentCorrectionFieldAltitudeDegrees = topocentric.Altitude.Degree;
        }

        private static double NormalizeSignedDegrees(double degrees) {
            while (degrees > 180.0) {
                degrees -= 360.0;
            }
            while (degrees < -180.0) {
                degrees += 360.0;
            }
            return degrees;
        }

        public TopocentricCoordinates GetDestinationCoordinates(double azAngle, double altAngle, RefractionParameters refractionParameters) {
            // Keep both transforms on the same clock as the frame onto which the destination is projected.
            var observationTime = CurrentReferenceFrame.Coordinates.DateTime;
            var referenceTopocentric = InitialReferenceFrame.Coordinates.Transform(Latitude,
                                                                                   Longitude,
                                                                                   Elevation,
                                                                                   0,
                                                                                   0,
                                                                                   0,
                                                                                   0,
                                                                                   observationTime.Now);
            var referenceVector = Vector3.CoordinatesToUnitVector(referenceTopocentric);

            var azimuthRotation = Angle.ByDegree(azAngle);
            var altitudeRotation = Angle.ByDegree(altAngle);

            var azimuthDestination = Vector3.RotateByRodrigues(referenceVector, new Vector3(0, 0, 1), azimuthRotation);
            var altitudeAxis = Vector3.RotateByRodrigues(new Vector3(0, 1, 0), new Vector3(0, 0, 1), azimuthRotation);
            var finalDestination = Vector3.RotateByRodrigues(azimuthDestination, altitudeAxis, altitudeRotation);

            return finalDestination.ToTopocentric(Latitude, Longitude, Elevation, observationTime);
        }

    }

    public class Position {
        public Position(Coordinates coordinates, double positionAngle, Angle latitude, Angle longitude, double elevation, RefractionParameters refractionParameters) {
            //if (refractionParameters != null) {
            double pressurehPa = refractionParameters.PressureHPa;
                double temperature = refractionParameters.Temperature;
                double relativeHumidity = refractionParameters.RelativeHumidity;
                double wavelength = refractionParameters.Wavelength;
                Logger.Info($"Transforming coordinates with refraction parameters. Pressure={pressurehPa}, Temperature={temperature}, Humidity={relativeHumidity}, Wavelength={wavelength}");
                Topocentric = coordinates.Transform(latitude, longitude, elevation, pressurehPa, temperature, relativeHumidity, wavelength, coordinates.DateTime.Now);
            //} else {
            //    Topocentric = coordinates.Transform(latitude, longitude, elevation);
            //}
            Vector = Vector3.CoordinatesToUnitVector(Topocentric);
            PositionAngle = positionAngle;
        }
        public Position(Vector3 vector, Angle latitude, Angle longitude, double elevation) {
            Topocentric = vector.ToTopocentric(latitude, longitude, elevation);
            Vector = vector;
        }

        public Position(TopocentricCoordinates coordinates) {
            Topocentric = coordinates;
            Vector = Vector3.CoordinatesToUnitVector(Topocentric);
        }

        public Vector3 Vector { get; }
        public TopocentricCoordinates Topocentric { get; }
        public double PositionAngle { get; }
    }

    public class TPPAStep : BaseINPC {
        public TPPAStep(string name) {
            Name = name;
            Completed = false;
            Active = false;
        }

        public string Name { get; }

        private bool relevant;
        public bool Relevant {
            get => relevant;
            set {
                relevant = value;
                RaisePropertyChanged();
            }
        }

        private bool active;
        public bool Active {
            get => active;
            set {
                active = value;
                RaisePropertyChanged();
            }
        }

        private bool completed;
        public bool Completed {
            get => completed;
            set {
                completed = value;
                RaisePropertyChanged();
            }
        }
    }
}
