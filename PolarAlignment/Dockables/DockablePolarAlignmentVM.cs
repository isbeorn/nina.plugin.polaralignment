using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NINA.Plugin.Interfaces;
using System.Reflection;
using Newtonsoft.Json;

namespace NINA.Plugins.PolarAlignment.Dockables {
    [Export(typeof(IDockableVM))]
    public class DockablePolarAlignmentVM : DockableVM , ICameraConsumer, ISubscriber {
        private IApplicationStatusMediator applicationStatusMediator;
        private ICameraMediator cameraMediator;
        private IWeatherDataMediator weatherDataMediator;
        private CancellationTokenSource executeCTS;
        private const string StartAlignmentTopic = $"{nameof(PolarAlignmentPlugin)}_{nameof(DockablePolarAlignmentVM)}_StartAlignment";
        private const string StopAlignmentTopic = $"{nameof(PolarAlignmentPlugin)}_{nameof(DockablePolarAlignmentVM)}_StopAlignment";

        [ImportingConstructor]
        public DockablePolarAlignmentVM(IProfileService profileService,
                                        IApplicationStatusMediator applicationStatusMediator,
                                        ICameraMediator cameraMediator,
                                        IImagingMediator imagingMediator,
                                        IFilterWheelMediator fwMediator,
                                        ITelescopeMediator telescopeMediator,
                                        IDomeMediator domeMediator,
                                        IPlateSolverFactory plateSolveFactory,
                                        IWeatherDataMediator weatherDataMediator,
                                        IMessageBroker messageBroker) : base(profileService) {
            Title = "Three Point Polar Alignment";
            OptionsExpanded = true;
            var dict = new ResourceDictionary();
            dict.Source = new Uri("NINA.Plugins.PolarAlignment;component/Options.xaml", UriKind.RelativeOrAbsolute);
            ImageGeometry = (System.Windows.Media.GeometryGroup)dict["ThreePointsSVG"];
            ImageGeometry.Freeze();

            this.profileService = profileService;
            this.applicationStatusMediator = applicationStatusMediator;
            this.cameraMediator = cameraMediator;
            this.weatherDataMediator = weatherDataMediator;

            this.PolarAlignment = new Instructions.PolarAlignment(profileService, cameraMediator, imagingMediator, fwMediator, telescopeMediator, plateSolveFactory, domeMediator, weatherDataMediator, new DummyService(), messageBroker);

            ExecuteCommand = new AsyncCommand<bool>(
                async () => { 
                    using (executeCTS = new CancellationTokenSource()) {
                        return await Execute(new Progress<ApplicationStatus>(p => Status = p), executeCTS.Token); 
                    }
                },
                (object o) => { return ((PolarAlignment as Instructions.PolarAlignment).Validate() && cameraMediator.IsFreeToCapture(this)); });

            PauseCommand = new RelayCommand(Pause, (object o) => !PolarAlignment.IsPausing);
            ResumeCommand = new RelayCommand(Resume);
            CancelExecuteCommand = new RelayCommand((object o) => { try { executeCTS?.Cancel(); } catch (Exception) { } });
            
            messageBroker.Subscribe(StartAlignmentTopic, this);
            messageBroker.Subscribe(StopAlignmentTopic, this);
        }
        

        private void Pause(object obj) {
            PolarAlignment.Pause();
        }

        private void Resume(object obj) {
            PolarAlignment.Resume();
        }

        public PolarAlignment.Instructions.PolarAlignment PolarAlignment { get; }

        private ApplicationStatus _status;

        public ApplicationStatus Status {
            get {
                return _status;
            }
            set {
                _status = value;
                if (string.IsNullOrWhiteSpace(_status.Source)) {
                    _status.Source = "TPPA";
                }

                RaisePropertyChanged();

                applicationStatusMediator.StatusUpdate(_status);
            }
        }

        public IAsyncCommand ExecuteCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand CancelExecuteCommand { get; }

        public override bool IsTool { get; } = true;

        private bool optionsExpanded;
        public bool OptionsExpanded {
            get => optionsExpanded;
            set {
                optionsExpanded = value;
                RaisePropertyChanged();
            }
        }
        private ApplicationStatus GetStatus(string status) {
            return new ApplicationStatus { Source = "TPPA", Status = status };
        }

        public async Task<bool> Execute(IProgress<ApplicationStatus> externalProgress, CancellationToken token) {
            try {
                OptionsExpanded = false;
                cameraMediator.RegisterCaptureBlock(this);
                PolarAlignment.ResetProgress();
                using (var localCTS = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    await PolarAlignment.Run(externalProgress, localCTS.Token);
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError(ex.Message);
            } finally {
                OptionsExpanded = true;
                cameraMediator.ReleaseCaptureBlock(this);
                externalProgress?.Report(GetStatus(string.Empty));
                (PolarAlignment as Instructions.PolarAlignment).TPAPAVM = new TPAPAVM(profileService, weatherDataMediator);
            }
            return false;
        }

        public void UpdateDeviceInfo(CameraInfo deviceInfo) {
        }

        public void Dispose() {
        }

        public class DummyService : IWindowService {
            protected Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            public event EventHandler OnDialogResultChanged;
            public event EventHandler OnClosed;

            public Task Close() {
                return Task.CompletedTask;
            }

            public void DelayedClose(TimeSpan t) {
            }

            public void Show(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None) {
            }

            public IDispatcherOperationWrapper ShowDialog(object content, string title = "", ResizeMode resizeMode = ResizeMode.NoResize, WindowStyle windowStyle = WindowStyle.None, ICommand closeCommand = null) {
                return new DispatcherOperationWrapper(dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { })));
            }
        }

        public async Task OnMessageReceived(IMessage message) {
            if (message.Topic == StartAlignmentTopic) {
                try {
                    try {
                        Logger.Info($"Received message to start polar alignment. {JsonConvert.SerializeObject(message.Content)}");
                    } catch (Exception) {
                        Logger.Info($"Received message to start polar alignment.");
                    }

                    //ManualMode
                    if(TryGetValue<bool>(message.Content, nameof(PolarAlignment.ManualMode), out var manualMode)) {
                        PolarAlignment.ManualMode = manualMode;
                    }
                    //TargetDistance
                    if (TryGetValue<int>(message.Content, nameof(PolarAlignment.TargetDistance), out var targetDistance)) {
                        PolarAlignment.TargetDistance = targetDistance;
                    }
                    //MoveRate
                    if (TryGetValue<int>(message.Content, nameof(PolarAlignment.MoveRate), out var moveRate)) {
                        PolarAlignment.MoveRate = moveRate;
                    }
                    //EastDirection
                    if (TryGetValue<bool>(message.Content, nameof(PolarAlignment.EastDirection), out var eastDirection)) {
                        PolarAlignment.EastDirection = eastDirection;
                    }
                    //StartFromCurrentPosition
                    if (TryGetValue<bool>(message.Content, nameof(PolarAlignment.StartFromCurrentPosition), out var startFromCurrentPosition)) {
                        PolarAlignment.StartFromCurrentPosition = startFromCurrentPosition;
                    }
                    //Coordinates
                    if (TryGetValue<int>(message.Content, nameof(PolarAlignment.Coordinates.AltDegrees), out var altDegrees)) {
                        PolarAlignment.Coordinates.AltDegrees = altDegrees;
                    }
                    if (TryGetValue<int>(message.Content, nameof(PolarAlignment.Coordinates.AltMinutes), out var altMinutes)) {
                        PolarAlignment.Coordinates.AltMinutes = altMinutes;
                    }
                    if (TryGetValue<double>(message.Content, nameof(PolarAlignment.Coordinates.AltSeconds), out var altSeconds)) {
                        PolarAlignment.Coordinates.AltSeconds = altSeconds;
                    }
                    if (TryGetValue<int>(message.Content, nameof(PolarAlignment.Coordinates.AzDegrees), out var azDegrees)) {
                        PolarAlignment.Coordinates.AzDegrees = azDegrees;
                    }
                    if (TryGetValue<int>(message.Content, nameof(PolarAlignment.Coordinates.AzMinutes), out var azMinutes)) {
                        PolarAlignment.Coordinates.AzMinutes = azMinutes;
                    }
                    if (TryGetValue<double>(message.Content, nameof(PolarAlignment.Coordinates.AzSeconds), out var azSeconds)) {
                        PolarAlignment.Coordinates.AzSeconds = azSeconds;
                    }
                    //AlignmentTolerance
                    if (TryGetValue<double>(message.Content, nameof(PolarAlignment.AlignmentTolerance), out var alignmentTolerance)) {
                        PolarAlignment.AlignmentTolerance = alignmentTolerance;
                    }
                    //Filter
                    if (TryGetValue<string>(message.Content, nameof(PolarAlignment.AlignmentTolerance), out var filterName)) {
                        var filter = profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Name == filterName);
                        if (filter != null) {
                            PolarAlignment.Filter = filter;
                        }                        
                    }
                    //ExposureTime
                    if (TryGetValue<double>(message.Content, nameof(PolarAlignment.ExposureTime), out var exposureTime)) {
                        PolarAlignment.ExposureTime = exposureTime;
                    }
                    //Binning
                    if (TryGetValue<short>(message.Content, nameof(PolarAlignment.Binning), out var binning)) {
                        PolarAlignment.Binning = new BinningMode(binning, binning);
                    }
                    //Gain
                    if (TryGetValue<int>(message.Content, nameof(PolarAlignment.Gain), out var gain)) {
                        PolarAlignment.Gain = gain;
                    }
                    //Offset
                    if (TryGetValue<int>(message.Content, nameof(PolarAlignment.Offset), out var offset)) {
                        PolarAlignment.Offset = offset;
                    }
                    //SearchRadius
                    if (TryGetValue<double>(message.Content, nameof(PolarAlignment.SearchRadius), out var searchRadius)) {
                        PolarAlignment.SearchRadius = searchRadius;
                    }

                    _ = ExecuteCommand.ExecuteAsync(null);
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            } else if (message.Topic == StopAlignmentTopic) {
                try {
                    Logger.Info("Received message to stop polar alignment");
                    executeCTS?.Cancel();
                } catch {}
            }
        }

        private static bool TryGetValue<T>(object obj, string name, out T value) {
            value = default!;
            if (obj == null || string.IsNullOrEmpty(name))
                return false;

            Type type = obj.GetType();

            // Check for public property
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanRead) {
                object propValue = property.GetValue(obj);
                if (propValue is T typedValue) {
                    value = typedValue;
                    return true;
                }
                return false;
            }

            // Check for public field
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) {
                object fieldValue = field.GetValue(obj);
                if (fieldValue is T typedValue) {
                    value = typedValue;
                    return true;
                }
                return false;
            }

            return false;
        }
    }
}