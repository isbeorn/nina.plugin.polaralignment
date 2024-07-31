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

        public TPAPAVM(IProfileService profileService, IWeatherDataMediator weatherDataMediator) {
            this.profileService = profileService;
            this.weatherDataMediator = weatherDataMediator;
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

        public void ActivateFirstStep() {
            lastMovement = null;
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

        public async Task SelectNewReferenceStar(Point p, CancellationToken token) {
            await selectNewStarLock.WaitAsync();
            try {
                ReferenceStar = await GetClosestStarPosition(Image, p, default, token);
                ReferenceStarCoordinates = PolarErrorDetermination.CurrentReferenceFrame.Coordinates.Shift(ReferenceStar.X - Center.X, ReferenceStar.Y - Center.Y, PolarErrorDetermination.CurrentReferenceFrame.Orientation, ArcsecPerPix, ArcsecPerPix);

                CalculateErrorDetails();
            } catch (Exception ex) {
                Logger.Error("An error occurred during selection of new reference star", ex);
                Notification.ShowWarning("Failed to determine new reference star on current image");
            } finally {
                selectNewStarLock.Release();
            }
        }


        public async Task UpdateDetails(PlateSolveResult psr, IProgress<ApplicationStatus> progress, CancellationToken token) {
            PolarErrorDetermination.CurrentReferenceFrame = psr;
            var currentCenter = PolarErrorDetermination.CurrentReferenceFrame;

            if ((psr.Coordinates - currentCenter.Coordinates).Distance.ArcSeconds > ArcsecPerPix) {
                // To minimize projection errors, try to re-acquire the same star from star detection instead of just projecting
                var p = ReferenceStarCoordinates.XYProjection(currentCenter.Coordinates, Center, ArcsecPerPix, ArcsecPerPix, currentCenter.Orientation);
                await SelectNewReferenceStar(p, token);
            } else {
                currentCenter = psr;
                ReferenceStar = ReferenceStarCoordinates.XYProjection(currentCenter.Coordinates, Center, ArcsecPerPix, ArcsecPerPix, currentCenter.Orientation);
                CalculateErrorDetails();
            }

            WaitingForUpdate = false;
        }

        private class Movement {
            public Movement(float azimuth, float altitude, float azimuthSign, float altitudeSign, double azimuthErrorBeforeMovement, double altitudeErrorBeforeMovement) {
                Azimuth = azimuth;
                Altitude = altitude;
                AzimuthSign = azimuthSign;
                AltitudeSign = altitudeSign;
                AzimuthErrorBeforeMovement = azimuthErrorBeforeMovement;
                AltitudeErrorBeforeMovement = altitudeErrorBeforeMovement;
            }

            public float Azimuth { get; }
            public float Altitude { get; }
            public float AzimuthSign { get; }
            public float AltitudeSign { get; }
            public double AzimuthErrorBeforeMovement { get; }
            public double AltitudeErrorBeforeMovement { get; }
        }

        private Movement lastMovement = null;
        public async Task MoveCloser(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (!UniversalPolarAlignmentVM.UsePolarAlignmentSystem || !UniversalPolarAlignmentVM.DoAutomatedAdjustments) { return; }

            var az = PolarErrorDetermination.CurrentMountAxisAzimuthError;
            var alt = PolarErrorDetermination.CurrentMountAxisAltitudeError;

            var azimuthSign = lastMovement?.AzimuthSign ?? 1f;
            var altitudeSign = lastMovement?.AltitudeSign ?? 1f;
            if (lastMovement != null) {
                if (lastMovement?.Altitude == 0) {
                    if (lastMovement.Azimuth != 0 && Math.Abs(az.Degree) > Math.Abs(lastMovement.AzimuthErrorBeforeMovement * 1.15d)) {
                        Logger.Info($"Reversing x axis movement as azimuth error is worse than before. Before: {lastMovement.AzimuthErrorBeforeMovement} - After: {az.Degree}");
                        // Axis is reversed
                        azimuthSign = -1f * lastMovement.AzimuthSign;
                    }
                } else if (lastMovement?.Azimuth == 0) {
                    if (lastMovement.Altitude != 0 && Math.Abs(alt.Degree) > Math.Abs(lastMovement.AltitudeErrorBeforeMovement * 1.15d)) {
                        Logger.Info($"Reversing y axis movement as altitude error is worse than before. Before: {lastMovement.AltitudeErrorBeforeMovement} - After: {alt.Degree}");
                        // Axis is reversed
                        altitudeSign = -1f * lastMovement.AltitudeSign;
                    }
                }
            }

            var xGreaterThanY = Math.Abs(az.Degree) > Math.Abs(alt.Degree);
            if (xGreaterThanY) {
                float azAdjustment = (float)az.ArcMinutes * azimuthSign * 0.75f;
                progress?.Report(new ApplicationStatus() { Status = $"Nudging UPA along X axis by {Math.Round(azAdjustment, 2)}" });
                await UniversalPolarAlignmentVM.NudgeX(azAdjustment, token);
                lastMovement = new Movement(azAdjustment, 0, azimuthSign, lastMovement?.AltitudeSign ?? 1f, az.Degree, alt.Degree);
            } else {
                float altAdjustment = (float)alt.ArcMinutes * altitudeSign * 0.75f;
                progress?.Report(new ApplicationStatus() { Status = $"Nudging UPA along Y axis by {Math.Round(altAdjustment, 2)}" });
                await UniversalPolarAlignmentVM.NudgeY(altAdjustment, token);
                lastMovement = new Movement(0, altAdjustment, lastMovement?.AzimuthSign ?? 1f, altitudeSign, az.Degree, alt.Degree);
            }

            await CoreUtil.Wait(TimeSpan.FromSeconds(UniversalPolarAlignmentVM.AutomatedAdjustmentSettleTime), token, progress, "Settling UPA");
        }

        private void CalculateErrorDetails() {

            var refractionParams = RefrectionParameters.GetRefrectionParameters(profileService.ActiveProfile.AstrometrySettings.Elevation, weatherDataMediator.GetInfo());
            var currentCenter = PolarErrorDetermination.CurrentReferenceFrame;

            var originPixel = PolarErrorDetermination.InitialReferenceFrame.Coordinates.XYProjection(currentCenter.Coordinates, Center, ArcsecPerPix, ArcsecPerPix, currentCenter.Orientation);


            var pointShift = Center - originPixel;
            originPixel = originPixel + pointShift * 2;

            var destinationAltAz = PolarErrorDetermination.GetDestinationCoordinates(-PolarErrorDetermination.InitialMountAxisAzimuthError.Degree, -PolarErrorDetermination.InitialMountAxisAltitudeError.Degree, refractionParams).Transform(Epoch.J2000);
            var destinationPixel = destinationAltAz.XYProjection(currentCenter.Coordinates, Center, ArcsecPerPix, ArcsecPerPix, currentCenter.Orientation);
            destinationPixel = destinationPixel + pointShift * 2;

            // Azimuth
            var originalAzimuthAltAz = PolarErrorDetermination.GetDestinationCoordinates(-PolarErrorDetermination.InitialMountAxisAzimuthError.Degree, 0, refractionParams).Transform(Epoch.J2000);
            var originalAzimuthPixel = originalAzimuthAltAz.XYProjection(currentCenter.Coordinates, Center, ArcsecPerPix, ArcsecPerPix, currentCenter.Orientation);
            originalAzimuthPixel = originalAzimuthPixel + pointShift * 2;

            var lineOriginToAzimuth = Line.FromPoints(ToAccordPoint(originPixel), ToAccordPoint(originalAzimuthPixel));

            var correctedAzimuthLine = Line.FromSlopeIntercept(lineOriginToAzimuth.Slope, (float)(Center.Y - lineOriginToAzimuth.Slope * Center.X));

            var lineAzimuthToDestination = Line.FromPoints(ToAccordPoint(originalAzimuthPixel), ToAccordPoint(destinationPixel));

            var correctedAzimuthPixel = lineAzimuthToDestination.GetIntersectionWith(correctedAzimuthLine); // az corrected position

            var correctedAzimuthDistance = correctedAzimuthPixel.Value.DistanceTo(ToAccordPoint(Center)); // az corrected
            var originalAzimuthDistance = ToAccordPoint(originalAzimuthPixel).DistanceTo(ToAccordPoint(originPixel)); // az orig

            // Altitude
            var originalAltitudeAltAz = PolarErrorDetermination.GetDestinationCoordinates(0, -PolarErrorDetermination.InitialMountAxisAltitudeError.Degree, refractionParams).Transform(Epoch.J2000);
            var originalAltitudePixel = originalAltitudeAltAz.XYProjection(currentCenter.Coordinates, Center, ArcsecPerPix, ArcsecPerPix, currentCenter.Orientation);
            originalAltitudePixel = originalAltitudePixel + pointShift * 2;

            var lineOriginToAltitude = Line.FromPoints(ToAccordPoint(originPixel), ToAccordPoint(originalAltitudePixel));

            var correctedAltitudeLine = Line.FromSlopeIntercept(lineOriginToAltitude.Slope, (float)(Center.Y - lineOriginToAltitude.Slope * Center.X));

            var lineAltitudeToDestination = Line.FromPoints(ToAccordPoint(originalAltitudePixel), ToAccordPoint(destinationPixel));

            var correctedAltitudePixel = lineAltitudeToDestination.GetIntersectionWith(correctedAltitudeLine); // alt corrected pixel

            var correctedAltitudeDistance = correctedAltitudePixel.Value.DistanceTo(ToAccordPoint(Center)); // alt corrected
            var originalAltitudeDistance = ToAccordPoint(originalAltitudePixel).DistanceTo(ToAccordPoint(originPixel)); // az corrected


            // Check if sign needs to be reversed

            // Azimuth
            var originalAzimuthDirection = ToAccordPoint(destinationPixel) - ToAccordPoint(originalAltitudePixel);
            var correctedAzimuthDirection = ToAccordPoint(destinationPixel) - correctedAltitudePixel.Value;
            // When dot product is positive, the angle between both vectors is smaller than 90°
            var azimuthSameDirection = (originalAzimuthDirection.X * correctedAzimuthDirection.X + originalAzimuthDirection.Y * correctedAzimuthDirection.Y) > 0;

            var azSign = 1;
            if (!azimuthSameDirection) {
                azSign = -1;
            }

            // Altitude
            var originalAltitudeDirection = ToAccordPoint(destinationPixel) - ToAccordPoint(originalAzimuthPixel);
            var correctedAltitudeDirection = ToAccordPoint(destinationPixel) - correctedAzimuthPixel.Value;
            // When dot product is positive, the angle between both vectors is smaller than 90°
            var AltitudeSameDirection = (originalAltitudeDirection.X * correctedAltitudeDirection.X + originalAltitudeDirection.Y * correctedAltitudeDirection.Y) > 0;

            var altSign = 1;
            if (!AltitudeSameDirection) {
                altSign = -1;
            }

            // Error determination

            PolarErrorDetermination.CurrentMountAxisAzimuthError = Angle.ByDegree(PolarErrorDetermination.InitialMountAxisAzimuthError.Degree * (azSign * correctedAzimuthDistance / originalAzimuthDistance));
            PolarErrorDetermination.CurrentMountAxisAltitudeError = Angle.ByDegree(PolarErrorDetermination.InitialMountAxisAltitudeError.Degree * (altSign * correctedAltitudeDistance / originalAltitudeDistance));
            PolarErrorDetermination.CurrentMountAxisTotalError = Angle.ByDegree(Accord.Math.Tools.Hypotenuse(PolarErrorDetermination.CurrentMountAxisAltitudeError.Degree, PolarErrorDetermination.CurrentMountAxisAzimuthError.Degree));

            var errorDetail = new ErrorDetail(Center, ToPoint(correctedAltitudePixel.Value), ToPoint(correctedAzimuthPixel.Value), destinationPixel);

            //errorDetail.Shift(pointShift);
            errorDetail.Shift(new System.Windows.Vector(ReferenceStar.X - Center.X, ReferenceStar.Y - Center.Y));
            ErrorDetail = errorDetail;

            var errorDetail2 = new ErrorDetail(originPixel, originalAltitudePixel, originalAzimuthPixel, destinationPixel);
            errorDetail2.Shift(new System.Windows.Vector(ReferenceStar.X - Center.X, ReferenceStar.Y - Center.Y));
            ErrorDetail2 = errorDetail2;

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

        public Accord.Point ToAccordPoint(Point p) {
            return new Accord.Point((float)p.X, (float)p.Y);
        }

        public Point ToPoint(Accord.Point p) {
            return new Point(p.X, p.Y);
        }

        public async Task<Point> GetClosestStarPosition(IRenderedImage image, Point reference, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var detection = await GetStarDetection(image, progress, token);

            var closestStarToReference = detection.StarList
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

                    var detectionResult = await detection.Detect(image, image.Image.Format, detectionParams, progress, token);

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
                UniversalPolarAlignmentVM.Disconnect();
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
        public PolarErrorDetermination(PlateSolveResult referenceFrame, Position position1, Position position2, Position position3, Angle latitude, Angle longitude, RefrectionParameters refrectionParameters) {
            Latitude = latitude;
            Longitude = longitude;

            InitialReferenceFrame = referenceFrame;
            FirstPosition = position1;
            SecondPosition = position2;
            ThirdPosition = position3;

            var planeVector = Vector3.DeterminePlaneVector(FirstPosition.Vector, SecondPosition.Vector, ThirdPosition.Vector);

            if ((Northern && planeVector.X < 0) || (!Northern && planeVector.X > 0)) {
                // Flip vector if pointing to the wrong direction
                planeVector = new Vector3(-planeVector.X, -planeVector.Y, -planeVector.Z);
            }


            InitialMountAxisErrorPosition = new Position(planeVector, Latitude, Longitude);

            CalculateMountAxisError(refrectionParameters);
            PositionAngleSpread = CalculatePositionAngleSpread(FirstPosition.PositionAngle, SecondPosition.PositionAngle, ThirdPosition.PositionAngle);
        }
        private double CalculatePositionAngleSpread(double value1, double value2, double value3) {
            var distance1 = CalculateDistance(value1, value2);
            var distance2 = CalculateDistance(value2, value3);
            var distance3 = CalculateDistance(value3, value1);

            return Math.Max(distance1, Math.Max(distance2, distance3));
        }
        private double CalculateDistance(double value1, double value2) {
            double directDistance = Math.Abs(value1 - value2);
            double wrapAroundDistance = 360 - directDistance;
            return Math.Min(directDistance, wrapAroundDistance);
        }

        public Angle Latitude { get; }
        public Angle Longitude { get; }
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

        public bool PositionAngleSpreadLarge {
            get => PositionAngleSpread > 5;
        }

        public bool InitialErrorLarge {
            get => InitialMountAxisTotalError.Degree > 2 && InitialMountAxisTotalError.Degree <= 10;
        }

        public bool InitialErrorHuge {
            get => InitialMountAxisTotalError.Degree > 10;
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

        public double PositionAngleSpread { get; }

        /// <summary>
        /// Calculate the error based on the measured telescope axis compared to the polar axis
        /// Polar axis = Azimuth 0 | Altitude = Latitude
        /// </summary>
        /// <returns></returns>
        private void CalculateMountAxisError(RefrectionParameters refrectionParameters) {
            var altitudeError = Angle.Zero;
            var azimuthError = Angle.Zero;

            var pole = Math.Abs(Latitude.Degree);
            if (refrectionParameters != null) {
                // When refraction is enabled, adjust pole to the refraction corrected pole
                pole = AstroUtil.CalculateRefractedAltitude(pole, refrectionParameters.PressureHPa, refrectionParameters.Temperature, refrectionParameters.RelativeHumidity, refrectionParameters.Wavelength);
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

        public TopocentricCoordinates GetDestinationCoordinates(double azAngle, double altAngle, RefrectionParameters refrectionParameters) {
            TopocentricCoordinates referenceTopo;
            if (refrectionParameters != null) {
                double pressurehPa = refrectionParameters.PressureHPa;
                double temperature = refrectionParameters.Temperature;
                double relativeHumidity = refrectionParameters.RelativeHumidity;
                double elevation = refrectionParameters.Elevation;
                double wavelength = refrectionParameters.Wavelength;
                Logger.Info($"Transforming coordinates with refraction parameters. Pressure={pressurehPa}, Temperature={temperature}, Humidity={relativeHumidity}, Wavelength={wavelength}");
                referenceTopo = InitialReferenceFrame.Coordinates.Transform(Latitude, Longitude, elevation, pressurehPa, temperature, relativeHumidity, wavelength);
            } else {
                referenceTopo = InitialReferenceFrame.Coordinates.Transform(Latitude, Longitude);
            }
            var vRef = Vector3.CoordinatesToUnitVector(referenceTopo);

            var azRotation = Angle.ByDegree(azAngle);
            var altRotation = Angle.ByDegree(altAngle);

            //First rotate by azimuth, then from the point at azimuth rotate further by altitude to get to the final position
            var azDest = Vector3.RotateByRodrigues(vRef, new Vector3(0, 0, 1), azRotation);
            var rotatedAltAxis = Vector3.RotateByRodrigues(new Vector3(0, 1, 0), new Vector3(0, 0, 1), azRotation);

            var finalDest = Vector3.RotateByRodrigues(azDest, rotatedAltAxis, altRotation); //combination of first az then applied alt

            return finalDest.ToTopocentric(Latitude, Longitude);
        }

    }

    public class Position {
        public Position(Coordinates coordinates, double positionAngle, Angle latitude, Angle longitude, RefrectionParameters refrectionParameters) {
            if (refrectionParameters != null) {
                double pressurehPa = refrectionParameters.PressureHPa;
                double temperature = refrectionParameters.Temperature;
                double relativeHumidity = refrectionParameters.RelativeHumidity;
                double elevation = refrectionParameters.Elevation;
                double wavelength = refrectionParameters.Wavelength;
                Logger.Info($"Transforming coordinates with refraction parameters. Pressure={pressurehPa}, Temperature={temperature}, Humidity={relativeHumidity}, Wavelength={wavelength}");
                Topocentric = coordinates.Transform(latitude, longitude, elevation, pressurehPa, temperature, relativeHumidity, wavelength);
            } else {
                Topocentric = coordinates.Transform(latitude, longitude);
            }
            Vector = Vector3.CoordinatesToUnitVector(Topocentric);
            PositionAngle = positionAngle;
        }
        public Position(Vector3 vector, Angle latitude, Angle longitude) {
            Topocentric = vector.ToTopocentric(latitude, longitude);
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
