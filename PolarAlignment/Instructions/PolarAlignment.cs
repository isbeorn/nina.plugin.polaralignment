using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NINA.Plugins.PolarAlignment.Instructions {
    /// <summary>
    /// This Item shows the basic principle on how to add a new Sequence Item to the N.I.N.A. sequencer via the plugin interface
    /// For ease of use this item inherits the abstract SequenceItem which already handles most of the running logic, like logging, exception handling etc.
    /// A complete custom implementation by just implementing ISequenceItem is possible too
    /// The following MetaData can be set to drive the initial values
    /// --> Name - The name that will be displayed for the item
    /// --> Description - a brief summary of what the item is doing. It will be displayed as a tooltip on mouseover in the application
    /// --> Icon - a string to the key value of a Geometry inside N.I.N.A.'s geometry resources
    ///
    /// If the item has some preconditions that should be validated, it shall also extend the IValidatable interface and add the validation logic accordingly.
    /// </summary>
    [ExportMetadata("Name", "Three Point Polar Alignment")]
    [ExportMetadata("Description", "Three Position Auto Polar Alignment anywhere in the sky")]
    [ExportMetadata("Icon", "ThreePointsSVG")]
    [ExportMetadata("Category", "Polar Alignment")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class PolarAlignment : SequenceItem, IValidatable {
        private IProfileService profileService;
        private ICameraMediator cameraMediator;
        private IImagingMediator imagingMediator;
        private IFilterWheelMediator fwMediator;
        private ITelescopeMediator telescopeMediator;
        private IWindowService windowService;
        private IPlateSolverFactory plateSolverFactory;
        private IDomeMediator domeMediator;
        private IWeatherDataMediator weatherDataMediator;
        private double moveRate;
        private double searchRadius;
        private int targetDistance;
        private bool eastDirection;
        private bool manualMode;
        private bool startFromCurrentPosition;
        private IList<string> issues = new List<string>();
        
        [OnDeserialized]
        public void OnDeserialized(StreamingContext context) {
            this.Filter = this.profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters?.FirstOrDefault(x => x.Name == this.Filter?.Name);
        }

        [ImportingConstructor]
        public PolarAlignment(IProfileService profileService, ICameraMediator cameraMediator, IImagingMediator imagingMediator, IFilterWheelMediator fwMediator, ITelescopeMediator telescopeMediator, IPlateSolverFactory plateSolverFactory, IDomeMediator domeMediator, IWeatherDataMediator weatherDataMediator) :this(profileService, cameraMediator, imagingMediator, fwMediator, telescopeMediator, plateSolverFactory, domeMediator, weatherDataMediator, new CustomWindowService()) {            
        }

        public PolarAlignment(IProfileService profileService, ICameraMediator cameraMediator, IImagingMediator imagingMediator, IFilterWheelMediator fwMediator, ITelescopeMediator telescopeMediator, IPlateSolverFactory plateSolverFactory, IDomeMediator domeMediator, IWeatherDataMediator weatherDataMediator, IWindowService windowService) {
            this.profileService = profileService;
            this.cameraMediator = cameraMediator;
            this.imagingMediator = imagingMediator;
            this.fwMediator = fwMediator;
            this.telescopeMediator = telescopeMediator;
            this.windowService = windowService;
            this.plateSolverFactory = plateSolverFactory;
            this.domeMediator = domeMediator;
            this.weatherDataMediator = weatherDataMediator;

            Filter = profileService.ActiveProfile.PlateSolveSettings.Filter;
            Gain = profileService.ActiveProfile.PlateSolveSettings.Gain;
            Offset = -1;
            ExposureTime = profileService.ActiveProfile.PlateSolveSettings.ExposureTime;
            Binning = new BinningMode(profileService.ActiveProfile.PlateSolveSettings.Binning, profileService.ActiveProfile.PlateSolveSettings.Binning);

            EastDirection = Properties.Settings.Default.DefaultEastDirection;
            MoveRate = Properties.Settings.Default.DefaultMoveRate;
            TargetDistance = Properties.Settings.Default.DefaultTargetDistance;
            SearchRadius = Properties.Settings.Default.DefaultSearchRadius;

            CameraInfo = this.cameraMediator.GetInfo();

            if (Northern) {
                Coordinates = new InputTopocentricCoordinates(new TopocentricCoordinates(Angle.ByDegree(Properties.Settings.Default.DefaultAzimuthOffset), Latitude + Angle.ByDegree(Properties.Settings.Default.DefaultAltitudeOffset), Latitude, Longitude, profileService.ActiveProfile.AstrometrySettings.Elevation, new SystemDateTime())); ;
            } else {
                Coordinates = new InputTopocentricCoordinates(new TopocentricCoordinates(Angle.ByDegree(180 + Properties.Settings.Default.DefaultAzimuthOffset), Angle.ByDegree(Math.Abs(Latitude.Degree)) + Angle.ByDegree(Properties.Settings.Default.DefaultAltitudeOffset), Latitude, Longitude, profileService.ActiveProfile.AstrometrySettings.Elevation, new SystemDateTime()));
            }

            TPAPAVM = new TPAPAVM(profileService, weatherDataMediator);
        }

        private PolarAlignment(PolarAlignment copyMe): this(copyMe.profileService, copyMe.cameraMediator, copyMe.imagingMediator, copyMe.fwMediator, copyMe.telescopeMediator, copyMe.plateSolverFactory, copyMe.domeMediator, copyMe.weatherDataMediator) {
            CopyMetaData(copyMe);
        }

        /// <summary>
        /// When items are put into the sequence via the factory, the factory will call the clone method. Make sure all the relevant fields are cloned with the object.
        /// </summary>
        /// <returns></returns>
        public override object Clone() {
            var clone = new PolarAlignment(this) {
                MoveRate = MoveRate,
                Filter = Filter,
                TargetDistance = TargetDistance,
                EastDirection = EastDirection,
                ExposureTime = ExposureTime,
                Binning = Binning,
                Gain = Gain,
                Offset = Offset,
                ManualMode = ManualMode,
                StartFromCurrentPosition = StartFromCurrentPosition,
                Coordinates = new InputTopocentricCoordinates(Coordinates.Coordinates.Copy())
            };

            if (clone.Binning == null) {
                clone.Binning = new BinningMode(1, 1);
            }

            return clone;
        }

        [JsonProperty]
        public InputTopocentricCoordinates Coordinates { get; set; }

        [JsonProperty]
        public double MoveRate {
            get => moveRate;
            set {
                moveRate = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public double SearchRadius {
            get => searchRadius;
            set {
                searchRadius = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public bool EastDirection {
            get => eastDirection;
            set {
                eastDirection = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public int TargetDistance {
            get => targetDistance;
            set {
                targetDistance = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public bool ManualMode {
            get => manualMode;
            set {
                manualMode = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public bool StartFromCurrentPosition {
            get => startFromCurrentPosition;
            set {
                startFromCurrentPosition = value;
                RaisePropertyChanged();
            }
        }



        private ApplicationStatus GetStatus(string status) {
            return new ApplicationStatus { Source = "TPPA", Status = status };
        }

        private TPAPAVM tpapa;
        public TPAPAVM TPAPAVM {
            get => tpapa;
            set {
                tpapa = value;
                RaisePropertyChanged();
            }
        }

        private async Task<PlateSolveResult> AutomatedNextPoint(IProgress<ApplicationStatus> progress, CancellationToken token) {
            PlateSolveResult solve;
            var totalDistance = (double)TargetDistance;
            var previousMountRADegrees = telescopeMediator.GetCurrentPosition().RADegrees;
            await MoveToNextPoint(totalDistance, MoveRate, progress, token);

            if(domeMediator.GetInfo().Connected) { 
                await domeMediator.WaitForDomeSynchronization(token);
            }

            solve = await Solve(TPAPAVM, progress, token);

            var distance = Distance(previousMountRADegrees, telescopeMediator.GetCurrentPosition().RADegrees);
            if (distance - totalDistance < -1) {

                Logger.Warning($"The mount did not move far enough to reach the target distance for the next point ({Math.Round(distance, 2)}°/{Math.Round(totalDistance, 2)}°).");
                Notification.ShowWarning($"The mount did not move far enough to reach the target distance for the next point ({Math.Round(distance, 2)}°/{Math.Round(totalDistance, 2)}°).{Environment.NewLine}This will happen when the mount driver's rate implementation is not according to the specifications to be degrees per seconds!{Environment.NewLine}Tip: Increase the slew rate and adjust the timeout setting inside the plugin options.");
            } 

            return solve;
        }

        private async Task<PlateSolveResult> ManualNextPoint(PlateSolveResult previousSolve, IProgress<ApplicationStatus> progress, CancellationToken token) {
            PlateSolveResult solve = null;
            bool traveledFarEnough;
            var totalDistance = (double)TargetDistance;

            var previousMountRADegrees = double.NaN;

            bool telescopeConnected = telescopeMediator.GetInfo().Connected;            

            if (telescopeConnected) {
                previousMountRADegrees = telescopeMediator.GetCurrentPosition().RADegrees;
            }
            
            do {        
                double distance;
                if (telescopeConnected) {
                    distance = Distance(previousMountRADegrees, telescopeMediator.GetCurrentPosition().RADegrees);
                } else {
                    // Without telescope connection, the coordinates need to be determined via solving
                    solve = await Solve(TPAPAVM, progress, token);
                    distance = Distance(previousSolve.Coordinates.RADegrees, solve.Coordinates.RADegrees);
                }
                if (distance - totalDistance < -1) {
                    traveledFarEnough = false;
                    
                    progress.Report(new ApplicationStatus() { Status = $"Move mount along RA axis! {Math.Round(distance, 2)}°/{Math.Round(totalDistance, 2)}°" });
                    await Task.Delay(TimeSpan.FromSeconds(1));
                } else {
                    traveledFarEnough = true;
                }
            } while (!traveledFarEnough);

            if (telescopeConnected) {
                while(telescopeMediator.GetInfo().Slewing) {
                    progress.Report(new ApplicationStatus() { Status = "Moved far enough. Stop axis rotation now!" });
                    await Task.Delay(500, token);
                }
                SetTrackingSidereal(true);
                await CoreUtil.Wait(TimeSpan.FromSeconds(profileService.ActiveProfile.TelescopeSettings.SettleTime), token, progress, "Settling");
                if (domeMediator.GetInfo().Connected) {
                    progress.Report(new ApplicationStatus() { Status = $"Waiting for dome to synchronize" });
                    await domeMediator.WaitForDomeSynchronization(token);
                }
                solve = await Solve(TPAPAVM, progress, token);                
            }

            return solve;
        }

        private void SetTrackingSidereal(bool onOff) {
            try {

                telescopeMediator.SetTrackingMode(Equipment.Interfaces.TrackingMode.Sidereal);
            } catch(Exception) { }
            try {
                telescopeMediator.SetTrackingEnabled(onOff);
            } catch(Exception) { }            
        }

        /// <summary>
        /// The core logic when the sequence item is running resides here
        /// Add whatever action is necessary
        /// </summary>
        /// <param name="progress">The application status progress that can be sent back during execution</param>
        /// <param name="token">When a cancel signal is triggered from outside, this token can be used to register to it or check if it is cancelled</param>
        /// <returns></returns>
        public override async Task Execute(IProgress<ApplicationStatus> externalProgress, CancellationToken token) {
            try {
                using (var localCTS = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    try {
                        TPAPAVM?.Dispose();
                    } catch(Exception) { }
                    
                    TPAPAVM = new TPAPAVM(profileService, weatherDataMediator);
                    IProgress<ApplicationStatus> progress = new Progress<ApplicationStatus>(p => { TPAPAVM.Status = p; externalProgress?.Report(p); });

                    windowService.Show(TPAPAVM, Loc.Instance["LblPolarAlignment"], System.Windows.ResizeMode.CanResizeWithGrip, System.Windows.WindowStyle.SingleBorderWindow);
                    windowService.OnClosed += (s, e) => {
                        try {
                            localCTS?.Cancel();
                        } catch (Exception) { }
                    };

                    TPAPAVM.ActivateFirstStep();

                    if(!ManualMode) { 
                        if(!StartFromCurrentPosition) {
                            Logger.Info($"Slewing to initial position {Coordinates.Coordinates}");
                            SetTrackingSidereal(true);
                            await telescopeMediator.SlewToCoordinatesAsync(Coordinates.Coordinates, localCTS.Token);
                        } else {
                            Logger.Info($"Starting from current position {telescopeMediator.GetCurrentPosition()}");
                        }

                    } else {
                        if(telescopeMediator.GetInfo().Connected) {
                            Logger.Info($"Manual mode engaged with mount connection available. Running in semi manual mode with standard plate solver.");
                            SetTrackingSidereal(true);
                        } else {
                            Logger.Info($"Manual mode engaged without any mount connection. Running in complete blind mode using blind solver.");
                        }
                        
                    }

                    var solve1 = await Solve(TPAPAVM, progress, localCTS.Token);

                    var telescopeInfo = telescopeMediator.GetInfo();

                    var position1 = new Position(solve1.Coordinates, Latitude, Longitude, RefrectionParameters.GetRefrectionParameters(profileService.ActiveProfile.AstrometrySettings.Elevation, weatherDataMediator.GetInfo()));

                    Logger.Info($"First measurement point {solve1.Coordinates} - Vector: {position1.Vector}");

                    TPAPAVM.ActivateSecondStep();

                    PlateSolveResult solve2 = null;
                    if (!ManualMode) {
                        solve2 = await AutomatedNextPoint(progress, localCTS.Token);
                    } else {
                        solve2 = await ManualNextPoint(solve1, progress, localCTS.Token);
                    }

                    var position2 = new Position(solve2.Coordinates, Latitude, Longitude, RefrectionParameters.GetRefrectionParameters(profileService.ActiveProfile.AstrometrySettings.Elevation, weatherDataMediator.GetInfo()));

                    Logger.Info($"Second measurement point {solve2.Coordinates} - Vector: {position2.Vector}");

                    TPAPAVM.ActivateThirdStep();

                    PlateSolveResult solve3 = null;
                    if (!ManualMode) {
                        solve3 = await AutomatedNextPoint(progress, localCTS.Token);
                    } else {
                        solve3 = await ManualNextPoint(solve2, progress, localCTS.Token);
                        await CoreUtil.Wait(TimeSpan.FromSeconds(10), localCTS.Token, progress, "Waiting for things to settle. Make sure the scope is tracking and don't move any further!");
                        solve3 = await Solve(TPAPAVM, progress, localCTS.Token);
                    }

                    var position3 = new Position(solve3.Coordinates, Latitude, Longitude, RefrectionParameters.GetRefrectionParameters(profileService.ActiveProfile.AstrometrySettings.Elevation, weatherDataMediator.GetInfo()));

                    Logger.Info($"Third measurement point {solve3.Coordinates} - Vector: {position3.Vector}");

                    progress?.Report(GetStatus("Calculating Error"));


                    TPAPAVM.PolarErrorDetermination = new PolarErrorDetermination(solve3, position1, position2, position3, Latitude, Longitude);

                    Logger.Info($"Calculated Error: Az: { TPAPAVM.PolarErrorDetermination.InitialMountAxisAzimuthError}, Alt: { TPAPAVM.PolarErrorDetermination.InitialMountAxisAltitudeError}, Tot: { TPAPAVM.PolarErrorDetermination.InitialMountAxisTotalError}");

                    TPAPAVM.ActivateFouthStep();

                    TPAPAVM.ArcsecPerPix = AstroUtil.ArcsecPerPixel(profileService.ActiveProfile.CameraSettings.PixelSize * Binning?.X ?? 1, profileService.ActiveProfile.TelescopeSettings.FocalLength);
                    var width = TPAPAVM.Image.Image.PixelWidth;
                    var height = TPAPAVM.Image.Image.PixelHeight;
                    TPAPAVM.Center = new Point(width / 2, height / 2);

                    await TPAPAVM.SelectNewReferenceStar(TPAPAVM.Center);


                    do {
                        var continuousSolve = await Solve(TPAPAVM, progress, localCTS.Token);
                        if (continuousSolve.Success) {
                            await TPAPAVM.UpdateDetails(continuousSolve);


                            var totalErrorMinutes = Math.Abs(TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes);
                            if (totalErrorMinutes <= Properties.Settings.Default.AlignmentTolerance) {
                                Logger.Info($"Total Error is below alignment tolerance ({Properties.Settings.Default.AlignmentTolerance}'). " +
                                    $"Altitude Error: {Math.Round(TPAPAVM.PolarErrorDetermination.CurrentMountAxisAltitudeError.ArcMinutes, 2)}'. " +
                                    $"Azimuth Error: {Math.Round(TPAPAVM.PolarErrorDetermination.CurrentMountAxisAzimuthError.ArcMinutes, 2)}'. " +
                                    $"Total Error: {Math.Round(totalErrorMinutes, 2)}'. " +
                                    $"Automatically finishing polar alignment.");
                                Notification.ShowInformation(
                                    $"Total Error is below alignment tolerance.{Environment.NewLine}" +
                                    $"Tolerance: {Properties.Settings.Default.AlignmentTolerance}{Environment.NewLine}'" +
                                    $"Altitude Error: {Math.Round(TPAPAVM.PolarErrorDetermination.CurrentMountAxisAltitudeError.ArcMinutes, 2)}'{Environment.NewLine}" +
                                    $"Azimuth Error: {Math.Round(TPAPAVM.PolarErrorDetermination.CurrentMountAxisAzimuthError.ArcMinutes, 2)}'{Environment.NewLine}" +
                                    $"Total Error: {Math.Round(totalErrorMinutes, 2)}'{Environment.NewLine}" +
                                    $"Automatically finishing polar alignment.", 
                                    TimeSpan.FromMinutes(1));
                                localCTS.Cancel();
                            }
                        }
                    } while (!localCTS.Token.IsCancellationRequested);

                    await windowService.Close();
                    return;
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError("Three Point Polar Alignment failed - " + ex.Message);
                await windowService?.Close();
                throw;
            } finally {
                externalProgress?.Report(GetStatus(string.Empty));
                if(Properties.Settings.Default.StopTrackingWhenDone) { 
                    SetTrackingSidereal(false);
                }
            }
        }

        public bool Northern {
            get => profileService.ActiveProfile.AstrometrySettings.Latitude > 0;
        }

        /// <summary>
        /// Calculate the error based on the measured telescope axis compared to the polar axis
        /// Polar axis = Azimuth 0 | Altitude = Latitude
        /// </summary>
        /// <param name="axis"></param>
        /// <returns></returns>
        private (Angle, Angle) CalculateError(TopocentricCoordinates axis) {
            if (Northern) {
                var altError = axis.Altitude - Latitude;
                var azError = axis.Azimuth;
                return (altError, azError);
            } else {
                var altError = axis.Altitude + Latitude;
                var azError = axis.Azimuth + Angle.ByDegree(180);
                return (altError, azError);
            }
        }

        private FilterInfo filter;

        [JsonProperty]
        public FilterInfo Filter { get => filter; set { filter = value; RaisePropertyChanged(); } }

        private double exposureTime;

        [JsonProperty]
        public double ExposureTime {
            get => exposureTime; set { exposureTime = value; RaisePropertyChanged(); }
        }

        private int gain;

        [JsonProperty]
        public int Gain { get => gain; 
            set { 
                gain = value; 
                RaisePropertyChanged(); 
            } 
        }

        private int offset;

        [JsonProperty]
        public int Offset { get => offset; set { offset = value; RaisePropertyChanged(); } }

        private BinningMode binning;

        [JsonProperty]
        public BinningMode Binning { get => binning; set { binning = value; RaisePropertyChanged(); } }

        private CameraInfo cameraInfo;

        public CameraInfo CameraInfo { get => cameraInfo; private set { cameraInfo = value; RaisePropertyChanged(); } }

        private async Task<PlateSolveResult> Solve(TPAPAVM context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            PlateSolveResult result = new PlateSolveResult { Success = false };

            do {
                token.ThrowIfCancellationRequested();

                var solver = plateSolverFactory.GetPlateSolver(profileService.ActiveProfile.PlateSolveSettings);
                IPlateSolver blindSolver = null;
                if(ManualMode && !telescopeMediator.GetInfo().Connected) {
                    Logger.Debug("Manual mode is enabled and no telescope is connected. Spawning the blind solver");
                    blindSolver = plateSolverFactory.GetBlindSolver(profileService.ActiveProfile.PlateSolveSettings);
                }

                var seq = new CaptureSequence() { Binning = Binning, Gain = Gain, ExposureTime = ExposureTime, Offset = Offset, FilterType = Filter };
                IRenderedImage image = null;
                try {
                    progress.Report(new ApplicationStatus() { Status = $"Capturing new image to solve..." });
                    image = await imagingMediator.CaptureAndPrepareImage(seq, new PrepareImageParameters(true, false), token, progress);
                } catch(Exception ex) {
                    Logger.Error(ex);
                }

                token.ThrowIfCancellationRequested();

                if (image != null) {
                    context.Image = image;

                    var imageSolver = plateSolverFactory.GetImageSolver(solver, blindSolver);

                    var parameter = new PlateSolveParameter() {
                        Binning = Binning?.X ?? 1,
                        Coordinates = telescopeMediator.GetCurrentPosition(),
                        DownSampleFactor = profileService.ActiveProfile.PlateSolveSettings.DownSampleFactor,
                        FocalLength = profileService.ActiveProfile.TelescopeSettings.FocalLength,
                        MaxObjects = profileService.ActiveProfile.PlateSolveSettings.MaxObjects,
                        PixelSize = profileService.ActiveProfile.CameraSettings.PixelSize,
                        Regions = profileService.ActiveProfile.PlateSolveSettings.Regions,
                        SearchRadius = SearchRadius,
                        DisableNotifications = true
                    };

                    progress.Report(new ApplicationStatus() { Status = $"Solving image..." });
                    result = await imageSolver.Solve(image.RawImageData, parameter, progress, token);
                    if (!result.Success) {
                        await CoreUtil.Wait(TimeSpan.FromSeconds(1), token, progress, "Plate solve failed. Retrying...");
                    }
                } else {
                    await CoreUtil.Wait(TimeSpan.FromSeconds(1), token, progress, "Image capture failed. Retrying...");
                }
            } while (result.Success == false);
            return result;
        }

        private double Distance(double raDegrees1, double raDegrees2) {
            return 180 - Math.Abs(Math.Abs(raDegrees1 - raDegrees2) - 180);
        }

        private async Task MoveToNextPoint(double moveDistance, double rate, IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                var startPosition = telescopeMediator.GetCurrentPosition();
                var currentPosition = telescopeMediator.GetCurrentPosition();
                var rates = telescopeMediator.GetInfo().PrimaryAxisRates;

                var foundRate = rates
                    .OrderBy(x => x.Item2)
                    .Where(x => x.Item1 <= rate && rate <= x.Item2 || x.Item2 < rate).LastOrDefault();

                var adjustedRate = rate;
                if(foundRate.Item2 < rate) {
                    Logger.Warning($"Provided MoveRate of {rate} is not supported. Using {adjustedRate} instead");
                    //The closest rate is below the specified move rate as no move rate is found for the value
                    adjustedRate = foundRate.Item2;
                }

                Logger.Info($"Moving axis by {adjustedRate} into direction {(EastDirection ? "East" : "West")} until distance {moveDistance}° is traveled");
                telescopeMediator.MoveAxis(Core.Enum.TelescopeAxes.Primary, EastDirection ? adjustedRate : -adjustedRate);

                //Move Rate is expectedly degree/s - Add a failsafe timer at 2x
                var timeToDestination = TimeSpan.FromSeconds(moveDistance / adjustedRate * Properties.Settings.Default.MoveTimeoutFactor);

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    try {
                        cts.CancelAfter(timeToDestination);                        

                        while (Distance(currentPosition.RADegrees, startPosition.RADegrees) < moveDistance) {
                            await Task.Delay(100, cts.Token);
                            currentPosition = telescopeMediator.GetCurrentPosition();

                            var distance = Distance(currentPosition.RADegrees, startPosition.RADegrees);

                            progress?.Report(new ApplicationStatus() { Status = "Moving to next point", MaxProgress = (int)moveDistance, Progress = distance, ProgressType = ApplicationStatus.StatusProgressType.ValueOfMaxValue });
                        }
                    } catch (OperationCanceledException ex) {
                        // Rethrow cancellation when parent token is cancelled
                        if (token.IsCancellationRequested) {
                            throw;
                        }
                    }
                }


                telescopeMediator.MoveAxis(Core.Enum.TelescopeAxes.Primary, 0);

                await CoreUtil.Wait(TimeSpan.FromSeconds(profileService.ActiveProfile.TelescopeSettings.SettleTime), token, progress, "Settling");
                SetTrackingSidereal(true);

                progress?.Report(new ApplicationStatus() { Status = string.Empty });
            } catch (Exception ex) {
                //Reset move rate in case of problems or early cancellation
                telescopeMediator.MoveAxis(Core.Enum.TelescopeAxes.Primary, 0);
                throw;
            }
        }

        public Angle Latitude {
            get => Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Latitude);
        }
        public Angle Longitude {
            get => Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Longitude);
        }

        /// <summary>
        /// This string will be used for logging
        /// </summary>
        /// <returns></returns>
        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(PolarAlignment)}, Rate: {MoveRate}, Distance: {TargetDistance}";
        }

        public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

        public bool Validate() {
            var i = new List<string>();

            //Location
            if(profileService.ActiveProfile.AstrometrySettings.Latitude == 0 && profileService.ActiveProfile.AstrometrySettings.Longitude == 0) {
                i.Add("No location has been set. Please set your latitude and longitude first as it is critical for the calculation to work!");
            }

            //Camera
            CameraInfo = this.cameraMediator.GetInfo();
            if (!CameraInfo.Connected) {
                i.Add(Loc.Instance["LblCameraNotConnected"]);
            } else {
                if (CameraInfo.CanSetGain && Gain > -1 && (Gain < CameraInfo.GainMin || Gain > CameraInfo.GainMax)) {
                    i.Add(string.Format(Loc.Instance["Lbl_SequenceItem_Imaging_TakeExposure_Validation_Gain"], CameraInfo.GainMin, CameraInfo.GainMax, Gain));
                }
                if (CameraInfo.CanSetOffset && Offset > -1 && (Offset < CameraInfo.OffsetMin || Offset > CameraInfo.OffsetMax)) {
                    i.Add(string.Format(Loc.Instance["Lbl_SequenceItem_Imaging_TakeExposure_Validation_Offset"], CameraInfo.OffsetMin, CameraInfo.OffsetMax, Offset));
                }
            }

            //Filter wheel
            if (filter != null && !fwMediator.GetInfo().Connected) {
                i.Add(Loc.Instance["LblFilterWheelNotConnected"]);
                i.Add("Either connect the filter wheel or clear the filter selection!");
            }

            //Mount
            var telescope = telescopeMediator.GetInfo();
            if (!ManualMode) { 
                if (!telescope.Connected) {
                    i.Add(Loc.Instance["LblTelescopeNotConnected"]);
                    i.Add("Switch to manual mode if no telescope connection is available");
                } else if (!telescope.CanMovePrimaryAxis) {
                    i.Add("Telescope cannot move primary axis. This is required for the automated slews around the right ascension axis!");
                }
            } 

            if(telescope.Connected && telescope.AtPark) {                    
                i.Add("Telescope is parked. Please unpark the telescope first!");
            }

            //Solver
            if(!ManualMode || (ManualMode && telescope.Connected)) {
                if (profileService.ActiveProfile.PlateSolveSettings.PlateSolverType == Core.Enum.PlateSolverEnum.ASTROMETRY_NET) {
                    i.Add("Astrometry.net is too slow for this method to work properly.");
                    i.Add("Please choose a different solver!");
                }
            } else {
                if (profileService.ActiveProfile.PlateSolveSettings.BlindSolverType == Core.Enum.BlindSolverEnum.ASTROMETRY_NET) {
                    i.Add("Blind solving is required without mount connection, but");
                    i.Add("Astrometry.net is too slow for this method to work properly.");
                    i.Add("Please choose a different solver!");
                }
            }
            

            Issues = i;
            return i.Count == 0;
        }

        public class CustomWindowService : IWindowService {
            protected Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            protected CustomWindow window;

            public void Show(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None) {
                dispatcher.Invoke(DispatcherPriority.Normal, new Action(() => {
                    window = new CustomWindow() {
                        SizeToContent = SizeToContent.Manual,
                        Title = title,
                        Background = Application.Current.TryFindResource("BackgroundBrush") as Brush,
                        ResizeMode = resizeMode,
                        WindowStyle = windowStyle,
                        MinHeight = 600,
                        MinWidth = 600,
                        Style = Application.Current.TryFindResource("NoResizeWindow") as Style,
                    };
                    window.CloseCommand = new RelayCommand((object o) => window.Close());
                    window.Closed += (object sender, EventArgs e) => this.OnClosed?.Invoke(this, null);
                    window.ContentRendered += (object sender, EventArgs e) => window.InvalidateVisual();
                    window.Content = content;
                    window.Owner = Application.Current.MainWindow;
                    window.Show();
                }));
            }

            public void DelayedClose(TimeSpan t) {
                Task.Run(async () => {
                    await CoreUtil.Wait(t);
                    await this.Close();
                });
            }

            public async Task Close() {
                await dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => {
                    window?.Close();
                }));
            }

            public IDispatcherOperationWrapper ShowDialog(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None, ICommand closeCommand = null) {
                return new DispatcherOperationWrapper(dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => {
                    window = new CustomWindow() {
                        SizeToContent = SizeToContent.WidthAndHeight,
                        Title = title,
                        Background = Application.Current.TryFindResource("BackgroundBrush") as Brush,
                        ResizeMode = resizeMode,
                        WindowStyle = windowStyle,
                        Style = Application.Current.TryFindResource("NoResizeWindow") as Style,
                    };
                    if (closeCommand == null) {
                        window.CloseCommand = new RelayCommand((object o) => { window.Close(); Application.Current.MainWindow.Focus(); });
                    } else {
                        window.CloseCommand = closeCommand;
                    }
                    window.Closed += (object sender, EventArgs e) => this.OnClosed?.Invoke(this, null);
                    window.ContentRendered += (object sender, EventArgs e) => window.InvalidateVisual();

                    window.SizeChanged += Win_SizeChanged;
                    window.Content = content;
                    var mainwindow = System.Windows.Application.Current.MainWindow;
                    mainwindow.Opacity = 0.8;
                    window.Owner = Application.Current.MainWindow;
                    var result = window.ShowDialog();
                    this.OnDialogResultChanged?.Invoke(this, new DialogResultEventArgs(result));
                    mainwindow.Opacity = 1;
                })));
            }

            public event EventHandler OnDialogResultChanged;

            public event EventHandler OnClosed;

            private static void Win_SizeChanged(object sender, SizeChangedEventArgs e) {
                var mainwindow = System.Windows.Application.Current.MainWindow;
                var win = (System.Windows.Window)sender;
                win.Left = mainwindow.Left + (mainwindow.Width - win.ActualWidth) / 2; ;
                win.Top = mainwindow.Top + (mainwindow.Height - win.ActualHeight) / 2;
            }
        }
    }
}