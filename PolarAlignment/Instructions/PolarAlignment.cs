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
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Plugins.PolarAlignment.Dockables;
using NINA.Plugins.PolarAlignment.Properties;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
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
using static NINA.Equipment.Model.CaptureSequence;
using static NINA.Plugins.PolarAlignment.Dockables.DockablePolarAlignmentVM;

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
    public class PolarAlignment : SequenceItem, IValidatable, ISubscriber {
        private IProfileService profileService;
        private ICameraMediator cameraMediator;
        private IImagingMediator imagingMediator;
        private IFilterWheelMediator fwMediator;
        private ITelescopeMediator telescopeMediator;
        private IWindowService windowService;
        private readonly IMessageBroker messageBroker;
        private IPlateSolverFactory plateSolverFactory;
        private IDomeMediator domeMediator;
        private IWeatherDataMediator weatherDataMediator;
        private PauseTokenSource pauseTS;
        private double moveRate;
        private double searchRadius;
        private int targetDistance;
        private bool eastDirection;
        private bool manualMode;
        private bool startFromCurrentPosition;
        private double alignmentTolerance;
        private IList<string> issues = new List<string>();
        private const string ResumeAlignmentTopic = $"{nameof(PolarAlignmentPlugin)}_{nameof(PolarAlignment)}_ResumeAlignment";
        private const string PauseAlignmentTopic = $"{nameof(PolarAlignmentPlugin)}_{nameof(PolarAlignment)}_PauseAlignment";

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {

        }

        [OnDeserialized]
        public void OnDeserialized(StreamingContext context) {
            MatchFilter();
        }

        private void MatchFilter() {
            try {
                var idx = this.Filter?.Position ?? -1;
                this.Filter = this.profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters?.FirstOrDefault(x => x.Name == this.Filter?.Name);
                if (this.Filter == null && idx >= 0) {
                    this.Filter = this.profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters?.FirstOrDefault(x => x.Position == idx);
                }
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        [ImportingConstructor]
        public PolarAlignment(
            IProfileService profileService,
            ICameraMediator cameraMediator,
            IImagingMediator imagingMediator,
            IFilterWheelMediator fwMediator,
            ITelescopeMediator telescopeMediator,
            IPlateSolverFactory plateSolverFactory,
            IDomeMediator domeMediator,
            IWeatherDataMediator weatherDataMediator,
            IMessageBroker messageBroker) : this(profileService, cameraMediator, imagingMediator, fwMediator, telescopeMediator, plateSolverFactory, domeMediator, weatherDataMediator, new CustomWindowService(), messageBroker) {
            
            Filter = null;
        }

        public PolarAlignment(
            IProfileService profileService,
            ICameraMediator cameraMediator,
            IImagingMediator imagingMediator,
            IFilterWheelMediator fwMediator,
            ITelescopeMediator telescopeMediator,
            IPlateSolverFactory plateSolverFactory,
            IDomeMediator domeMediator,
            IWeatherDataMediator weatherDataMediator,
            IWindowService windowService,
            IMessageBroker messageBroker) {
            
            this.profileService = profileService;
            this.cameraMediator = cameraMediator;
            this.imagingMediator = imagingMediator;
            this.fwMediator = fwMediator;
            this.telescopeMediator = telescopeMediator;
            this.windowService = windowService;
            this.messageBroker = messageBroker;
            this.plateSolverFactory = plateSolverFactory;
            this.domeMediator = domeMediator;
            this.weatherDataMediator = weatherDataMediator;

            if (windowService is DummyService) {
                Filter = profileService.ActiveProfile.PlateSolveSettings.Filter;
            }
            Gain = profileService.ActiveProfile.PlateSolveSettings.Gain;
            Offset = -1;
            ExposureTime = profileService.ActiveProfile.PlateSolveSettings.ExposureTime;

            EastDirection = Properties.Settings.Default.DefaultEastDirection;
            MoveRate = Properties.Settings.Default.DefaultMoveRate;
            TargetDistance = Properties.Settings.Default.DefaultTargetDistance;
            SearchRadius = Properties.Settings.Default.DefaultSearchRadius;
            AlignmentTolerance = Properties.Settings.Default.AlignmentTolerance;

            CameraInfo = this.cameraMediator.GetInfo();

            if (Northern) {
                Coordinates = new InputTopocentricCoordinates(new TopocentricCoordinates(Angle.ByDegree(Properties.Settings.Default.DefaultAzimuthOffset), Latitude + Angle.ByDegree(Properties.Settings.Default.DefaultAltitudeOffset), Latitude, Longitude, profileService.ActiveProfile.AstrometrySettings.Elevation, new SystemDateTime()));
                ;
            } else {
                Coordinates = new InputTopocentricCoordinates(new TopocentricCoordinates(Angle.ByDegree(180 + Properties.Settings.Default.DefaultAzimuthOffset), Angle.ByDegree(Math.Abs(Latitude.Degree)) + Angle.ByDegree(Properties.Settings.Default.DefaultAltitudeOffset), Latitude, Longitude, profileService.ActiveProfile.AstrometrySettings.Elevation, new SystemDateTime()));
            }

            TPAPAVM = new TPAPAVM(profileService, weatherDataMediator);
            this.messageBroker.Subscribe(ResumeAlignmentTopic, this);
            this.messageBroker.Subscribe(PauseAlignmentTopic, this);
        }

        private PolarAlignment(PolarAlignment copyMe) : this(copyMe.profileService, copyMe.cameraMediator, copyMe.imagingMediator, copyMe.fwMediator, copyMe.telescopeMediator, copyMe.plateSolverFactory, copyMe.domeMediator, copyMe.weatherDataMediator, copyMe.messageBroker) {
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
                Binning = Binning == null ? null : new BinningMode(Binning.X, Binning.Y),
                Gain = Gain,
                Offset = Offset,
                ManualMode = ManualMode,
                StartFromCurrentPosition = StartFromCurrentPosition,
                AlignmentTolerance = AlignmentTolerance,
                Coordinates = new InputTopocentricCoordinates(Coordinates.Coordinates.Copy())
            };

            if (clone.Binning == null) {
                Binning = new BinningMode(profileService.ActiveProfile.PlateSolveSettings.Binning, profileService.ActiveProfile.PlateSolveSettings.Binning);
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
                searchRadius = Math.Max(30, Math.Min(180, value));
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

        [JsonProperty]
        public double AlignmentTolerance {
            get => alignmentTolerance;
            set {
                alignmentTolerance = value;
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

            await WaitIfPaused(token, progress);
            await MoveToNextPoint(totalDistance, MoveRate, progress, token);

            if (domeMediator.GetInfo().Connected) {
                await domeMediator.WaitForDomeSynchronization(token);
            }

            solve = await Solve(TPAPAVM, 5.0, progress, token);

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
                await WaitIfPaused(token, progress);
                double distance;
                if (telescopeConnected) {
                    distance = Distance(previousMountRADegrees, telescopeMediator.GetCurrentPosition().RADegrees);
                } else {
                    // Without telescope connection, the coordinates need to be determined via solving
                    solve = await Solve(TPAPAVM, 5.0, progress, token);
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
                while (telescopeMediator.GetInfo().Slewing) {
                    progress.Report(new ApplicationStatus() { Status = "Moved far enough. Stop axis rotation now!" });
                    await Task.Delay(500, token);
                }
                await CoreUtil.Wait(TimeSpan.FromSeconds(profileService.ActiveProfile.TelescopeSettings.SettleTime), token, progress, "Settling");
                SetTrackingSidereal(true);
                if (domeMediator.GetInfo().Connected) {
                    progress.Report(new ApplicationStatus() { Status = $"Waiting for dome to synchronize" });
                    await domeMediator.WaitForDomeSynchronization(token);
                }
                solve = await Solve(TPAPAVM, 5.0, progress, token);
            }

            return solve;
        }

        private void SetTrackingSidereal(bool onOff) {
            try {

                telescopeMediator.SetTrackingMode(Equipment.Interfaces.TrackingMode.Sidereal);
            } catch (Exception) { }
            try {
                telescopeMediator.SetTrackingEnabled(onOff);
            } catch (Exception) { }
        }

        public void Pause() {
            if (pauseTS != null) {
                pauseTS.IsPaused = true;
                RaisePropertyChanged(nameof(IsPaused));
            }
        }
        public void Resume() {
            if (pauseTS != null) {
                pauseTS.IsPaused = false;
                RaisePropertyChanged(nameof(IsPaused));
            }
        }

        private bool isPaused;
        public bool IsPaused {
            get => isPaused;
            private set {
                isPaused = value;
                RaisePropertyChanged();
            }
        }
        public bool IsPausing { get => pauseTS?.IsPaused ?? false; }

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
                    pauseTS = new PauseTokenSource();
                    try {
                        TPAPAVM?.Dispose();
                    } catch { }

                    TPAPAVM = new TPAPAVM(profileService, weatherDataMediator);
                    IProgress<ApplicationStatus> progress = new Progress<ApplicationStatus>(p => {
                        TPAPAVM.Status = p;
                        externalProgress?.Report(p);
                    });

                    windowService.Show(TPAPAVM, Loc.Instance["LblPolarAlignment"], System.Windows.ResizeMode.CanResizeWithGrip, System.Windows.WindowStyle.SingleBorderWindow);
                    windowService.OnClosed += (s, e) => {
                        try {
                            localCTS?.Cancel();
                        } catch { }
                    };

                    var currentPosition = telescopeMediator.GetInfo().Connected ? telescopeMediator.GetCurrentPosition().Transform(Latitude, Longitude) : null;
                    Logger.Info($"""
                        Starting polar alignment:
                            Manual mode: {ManualMode}
                            Measure point distance: {TargetDistance}
                            Mount move rate: {MoveRate}
                            Timeout factor: {Properties.Settings.Default.MoveTimeoutFactor}
                            Direction east: {EastDirection}
                            Start from current: {StartFromCurrentPosition}
                            Altitude: {(StartFromCurrentPosition ? currentPosition?.Altitude : Coordinates.Coordinates.Altitude)}
                            Azimuth: {(StartFromCurrentPosition ? currentPosition?.Azimuth : Coordinates.Coordinates.Azimuth)}
                            Alignment tolerance: {AlignmentTolerance}
                            Filter: {Filter}
                            Exposure time: {ExposureTime}
                            Binning: {Binning}
                            Gain: {Gain}
                            Offset: {Offset}
                            Initial search radius: {SearchRadius}
                            Refraction adjustment: {Properties.Settings.Default.RefractionAdjustment}
                            Stop tracking when done: {Properties.Settings.Default.StopTrackingWhenDone}
                            Auto pause: {Properties.Settings.Default.AutoPause}
                            Avalon UPA: {Properties.Settings.Default.UseAvalonPolarAlignmentSystem}
                            Automated adjustments: {Properties.Settings.Default.DoAutomatedAdjustments}
                        """);

                    TPAPAVM.ActivateFirstStep();

                    if (!ManualMode) {
                        if (!StartFromCurrentPosition) {
                            Logger.Info($"Slewing to initial position {Coordinates.Coordinates}");
                            SetTrackingSidereal(true);
                            await telescopeMediator.SlewToCoordinatesAsync(Coordinates.Coordinates, localCTS.Token);
                        } else {
                            Logger.Info($"Starting from current position {telescopeMediator.GetCurrentPosition()}");
                        }

                    } else {
                        if (telescopeMediator.GetInfo().Connected) {
                            Logger.Info($"Manual mode engaged with mount connection available. Running in semi manual mode with standard plate solver.");
                            SetTrackingSidereal(true);
                        } else {
                            Logger.Info($"Manual mode engaged without any mount connection. Running in complete blind mode using blind solver.");
                        }
                    }

                    if (telescopeMediator.GetInfo().Connected && domeMediator.GetInfo().Connected) {
                        await domeMediator.WaitForDomeSynchronization(token);
                    }

                    var solve1 = await Solve(TPAPAVM, 5.0, progress, localCTS.Token);
                    var refractionParameter = RefractionParameters.GetRefractionParameters(weatherDataMediator.GetInfo());

                    var telescopeInfo = telescopeMediator.GetInfo();

                    var position1 = new Position(solve1.Coordinates, solve1.PositionAngle, Latitude, Longitude, Elevation, refractionParameter);

                    Logger.Info($"First measurement point {solve1.Coordinates} - Vector: {position1.Vector} - Position Angle: {position1.PositionAngle}");

                    TPAPAVM.ActivateSecondStep();

                    PlateSolveResult solve2 = null;
                    if (!ManualMode) {
                        solve2 = await AutomatedNextPoint(progress, localCTS.Token);
                    } else {
                        solve2 = await ManualNextPoint(solve1, progress, localCTS.Token);
                    }

                    var position2 = new Position(solve2.Coordinates, solve2.PositionAngle, Latitude, Longitude, Elevation, refractionParameter);

                    Logger.Info($"Second measurement point {solve2.Coordinates} - Vector: {position2.Vector} - Position Angle: {position2.PositionAngle}");

                    TPAPAVM.ActivateThirdStep();


                    PlateSolveResult solve3 = null;
                    if (!ManualMode) {
                        solve3 = await AutomatedNextPoint(progress, localCTS.Token);
                    } else {
                        solve3 = await ManualNextPoint(solve2, progress, localCTS.Token);
                        await CoreUtil.Wait(TimeSpan.FromSeconds(10), localCTS.Token, progress, "Waiting for things to settle. Make sure the scope is tracking and don't move any further!");
                        solve3 = await Solve(TPAPAVM, 5.0, progress, localCTS.Token);
                    }

                    var position3 = new Position(solve3.Coordinates, solve3.PositionAngle, Latitude, Longitude, Elevation, refractionParameter);

                    Logger.Info($"Third measurement point {solve3.Coordinates} - Vector: {position3.Vector} - Position Angle: {position3.PositionAngle}");

                    progress?.Report(GetStatus("Calculating Error"));


                    TPAPAVM.PolarErrorDetermination = new PolarErrorDetermination(solve3, position1, position2, position3, Latitude, Longitude, Elevation, refractionParameter, Properties.Settings.Default.RefractionAdjustment);

                    Logger.Info($"Calculated Error: Az: {TPAPAVM.PolarErrorDetermination.InitialMountAxisAzimuthError}, Alt: {TPAPAVM.PolarErrorDetermination.InitialMountAxisAltitudeError}, Tot: {TPAPAVM.PolarErrorDetermination.InitialMountAxisTotalError}");

                    TPAPAVM.ActivateFourthStep();

                    if (TPAPAVM.UniversalPolarAlignmentVM.UsePolarAlignmentSystem) {
                        await TPAPAVM.UniversalPolarAlignmentVM.Connect();
                        if (TPAPAVM.UniversalPolarAlignmentVM.DoAutomatedAdjustments && !TPAPAVM.UniversalPolarAlignmentVM.Connected) {
                            throw new SequenceEntityFailedException("Unable to connect to Universal Polar Alignment system. Cancelling polar alignment routine as automated adjustments are impossible.");
                        }
                    }

                    TPAPAVM.ArcsecPerPix = AstroUtil.ArcsecPerPixel(profileService.ActiveProfile.CameraSettings.PixelSize * Binning?.X ?? 1, profileService.ActiveProfile.TelescopeSettings.FocalLength);
                    var width = TPAPAVM.Image.Image.PixelWidth;
                    var height = TPAPAVM.Image.Image.PixelHeight;
                    TPAPAVM.Center = new Point(width / 2, height / 2);

                    await TPAPAVM.SelectNewReferenceStar(TPAPAVM.Center, localCTS.Token);

                    var sw = Stopwatch.StartNew();
                    Guid correlatedGuid = Guid.NewGuid();
                    do {
                        await WaitIfPaused(localCTS.Token, progress);

                        var continuousSolve = await Solve(TPAPAVM, 0, progress, localCTS.Token);
                        if (continuousSolve.Success) {
                            await TPAPAVM.UpdateDetails(continuousSolve, progress, localCTS.Token);

                            await messageBroker.Publish(
                                new PolarAlignmentErrorMessage(
                                    correlatedGuid,
                                    altitudeError: TPAPAVM.PolarErrorDetermination.CurrentMountAxisAltitudeError.Degree,
                                    azimuthError: TPAPAVM.PolarErrorDetermination.CurrentMountAxisAzimuthError.Degree,
                                    totalError: TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.Degree
                                )
                            );

                            Logger.Info($"Calculated Error: Az: {TPAPAVM.PolarErrorDetermination.CurrentMountAxisAzimuthError}, Alt: {TPAPAVM.PolarErrorDetermination.CurrentMountAxisAltitudeError}, Tot: {TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError}");

                            var totalErrorMinutes = Math.Abs(TPAPAVM.PolarErrorDetermination.CurrentMountAxisTotalError.ArcMinutes);
                            if (totalErrorMinutes <= AlignmentTolerance) {
                                Logger.Info($"Total Error is below alignment tolerance ({AlignmentTolerance}'). " +
                                    $"Altitude Error: {Math.Round(TPAPAVM.PolarErrorDetermination.CurrentMountAxisAltitudeError.ArcMinutes, 2)}'. " +
                                    $"Azimuth Error: {Math.Round(TPAPAVM.PolarErrorDetermination.CurrentMountAxisAzimuthError.ArcMinutes, 2)}'. " +
                                    $"Total Error: {Math.Round(totalErrorMinutes, 2)}'. " +
                                    $"Automatically finishing polar alignment.");
                                Notification.ShowInformation(
                                    $"Total Error is below alignment tolerance.{Environment.NewLine}" +
                                    $"Tolerance: {AlignmentTolerance}{Environment.NewLine}'" +
                                    $"Altitude Error: {Math.Round(TPAPAVM.PolarErrorDetermination.CurrentMountAxisAltitudeError.ArcMinutes, 2)}'{Environment.NewLine}" +
                                    $"Azimuth Error: {Math.Round(TPAPAVM.PolarErrorDetermination.CurrentMountAxisAzimuthError.ArcMinutes, 2)}'{Environment.NewLine}" +
                                    $"Total Error: {Math.Round(totalErrorMinutes, 2)}'{Environment.NewLine}" +
                                    $"Automatically finishing polar alignment.",
                                    TimeSpan.FromMinutes(1));
                                localCTS.Cancel();
                            }
                            if (sw.Elapsed > TimeSpan.FromMinutes(5)) {
                                Logger.Info("Correction phase exceeded 5 minutes");
                                Notification.ShowInformation($"Polar alignment correction phase has been running for multiple minutes.{Environment.NewLine}Consider restarting the process to improve precision");
                                sw.Stop();
                                sw.Reset();
                            }
                            localCTS.Token.ThrowIfCancellationRequested();
                            await TPAPAVM.MoveCloser(progress, localCTS.Token);
                        }

                        if (Properties.Settings.Default.AutoPause) {
                            Pause();
                        }
                    } while (!localCTS.Token.IsCancellationRequested);

                    return;
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError("Three Point Polar Alignment failed - " + ex.Message);
                throw;
            } finally {
                try {
                    await windowService?.Close();
                } catch { }
                try {
                    TPAPAVM?.Dispose();
                } catch (Exception) { }
                IsPaused = false;
                externalProgress?.Report(GetStatus(string.Empty));
                if (Properties.Settings.Default.StopTrackingWhenDone) {
                    SetTrackingSidereal(false);
                }
            }
        }

        private async Task WaitIfPaused(CancellationToken token, IProgress<ApplicationStatus> progress) {
            if (IsPausing) {
                IsPaused = true;
                progress?.Report(GetStatus("Paused"));
                await pauseTS.Token.WaitWhilePausedAsync(token);
                progress?.Report(GetStatus(string.Empty));
                IsPaused = false;
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
        public FilterInfo Filter {
            get => filter;
            set {
                filter = value;
                RaisePropertyChanged();
            }
        }

        private double exposureTime;

        [JsonProperty]
        public double ExposureTime {
            get => exposureTime;
            set {
                exposureTime = value;
                RaisePropertyChanged();
            }
        }

        private int gain;

        [JsonProperty]
        public int Gain {
            get => gain;
            set {
                gain = value;
                RaisePropertyChanged();
            }
        }

        private int offset;

        [JsonProperty]
        public int Offset {
            get => offset;
            set {
                offset = value;
                RaisePropertyChanged();
            }
        }

        private BinningMode binning;

        [JsonProperty]
        public BinningMode Binning {
            get => binning;
            set {
                binning = value;
                RaisePropertyChanged();
            }
        }

        private CameraInfo cameraInfo;

        public CameraInfo CameraInfo {
            get => cameraInfo;
            private set {
                cameraInfo = value;
                RaisePropertyChanged();
            }
        }

        private async Task<PlateSolveResult> Solve(TPAPAVM context, double searchRadiusIncrementOnFailure, IProgress<ApplicationStatus> progress, CancellationToken token) {
            PlateSolveResult result = new PlateSolveResult { Success = false };
            double usedSearchRadius = SearchRadius;
            do {
                token.ThrowIfCancellationRequested();

                var solver = plateSolverFactory.GetPlateSolver(profileService.ActiveProfile.PlateSolveSettings);
                IPlateSolver blindSolver = null;
                if (ManualMode && !telescopeMediator.GetInfo().Connected) {
                    Logger.Debug("Manual mode is enabled and no telescope is connected. Spawning the blind solver");
                    blindSolver = plateSolverFactory.GetBlindSolver(profileService.ActiveProfile.PlateSolveSettings);
                }

                var seq = new CaptureSequence() { Binning = Binning, Gain = Gain, ExposureTime = ExposureTime, Offset = Offset, FilterType = Filter, ImageType = ImageTypes.SNAPSHOT };
                IRenderedImage image = null;
                try {
                    progress.Report(new ApplicationStatus() { Status = $"Capturing new image to solve..." });
                    image = await imagingMediator.CaptureAndPrepareImage(seq, new PrepareImageParameters(true, false), token, progress);
                } catch (Exception ex) {
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
                        SearchRadius = usedSearchRadius,
                        DisableNotifications = true
                    };

                    progress.Report(new ApplicationStatus() { Status = $"Solving image..." });
                    result = await imageSolver.Solve(image.RawImageData, parameter, progress, token);
                    if (!result.Success) {
                        usedSearchRadius += searchRadiusIncrementOnFailure;
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
                    .OrderBy(x => x.Item2).LastOrDefault(x => x.Item1 <= rate && rate <= x.Item2 || x.Item2 < rate);

                var adjustedRate = rate;
                if (foundRate.Item2 < rate) {
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

                while (telescopeMediator.GetInfo().Slewing) {
                    await CoreUtil.Wait(TimeSpan.FromMilliseconds(500), token, progress, "Waiting for mount to stop slewing");
                }
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
        public double Elevation {
            get => profileService.ActiveProfile.AstrometrySettings.Elevation;
        }

        /// <summary>
        /// This string will be used for logging
        /// </summary>
        /// <returns></returns>
        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(PolarAlignment)}, Rate: {MoveRate}, Distance: {TargetDistance}";
        }

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        public bool Validate() {
            var i = new List<string>();

            //Location
            if (profileService.ActiveProfile.AstrometrySettings.Latitude == 0 && profileService.ActiveProfile.AstrometrySettings.Longitude == 0) {
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

            if (telescope.Connected && telescope.AtPark) {
                i.Add("Telescope is parked. Please unpark the telescope first!");
            }

            //Solver
            if (!ManualMode || (ManualMode && telescope.Connected)) {
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

            if (Settings.Default.UseAvalonPolarAlignmentSystem && Settings.Default.DoAutomatedAdjustments && AlignmentTolerance == 0) {
                i.Add("Automated adjustments are enabled, but polar alignment tolerance is set to zero. Please set an alignment tolerance!");
            }


            Issues = i;
            return i.Count == 0;
        }

        public async Task OnMessageReceived(IMessage message) {
            if (message.Topic == ResumeAlignmentTopic) {
                try {
                    Logger.Info("Received message to resume polar alignment");
                    Resume();
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            } else if (message.Topic == PauseAlignmentTopic) {
                try {
                    Logger.Info("Received message to pause polar alignment");
                    Pause();
                } catch { }
            }
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
                        window.CloseCommand = new RelayCommand((object o) => {
                            window.Close();
                            Application.Current.MainWindow?.Focus();
                        });
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
                win.Left = mainwindow.Left + (mainwindow.Width - win.ActualWidth) / 2;
                ;
                win.Top = mainwindow.Top + (mainwindow.Height - win.ActualHeight) / 2;
            }
        }
    }
    
    public class PolarAlignmentErrorMessage(Guid correlatedGuid, double altitudeError, double azimuthError, double totalError) : IMessage {

        public Guid SenderId => Guid.Parse(PolarAlignmentPlugin.PluginId);

        public string Sender => nameof(PolarAlignmentPlugin);

        public DateTimeOffset SentAt => DateTime.UtcNow;

        public Guid MessageId => Guid.NewGuid();

        public DateTimeOffset? Expiration => null;

        public Guid? CorrelationId => correlatedGuid;

        public int Version => 1;

        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();

        public string Topic => $"{nameof(PolarAlignmentPlugin)}_{nameof(PolarAlignment)}_AlignmentError";

        public object Content { get; } = new {
            AzimuthError = azimuthError,
            AltitudeError = altitudeError,
            TotalError = totalError
        };
    }
}
