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

namespace NINA.Plugins.PolarAlignment.Dockables {
    [Export(typeof(IDockableVM))]
    public class DockablePolarAlignmentVM : DockableVM , ICameraConsumer{
        private IApplicationStatusMediator applicationStatusMediator;
        private ICameraMediator cameraMediator;
        private IWeatherDataMediator weatherDataMediator;
        private CancellationTokenSource executeCTS;

        [ImportingConstructor]
        public DockablePolarAlignmentVM(IProfileService profileService, IApplicationStatusMediator applicationStatusMediator, ICameraMediator cameraMediator, IImagingMediator imagingMediator, IFilterWheelMediator fwMediator, ITelescopeMediator telescopeMediator, IDomeMediator domeMediator, IPlateSolverFactory plateSolveFactory, IWeatherDataMediator weatherDataMediator) : base(profileService) {
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

            this.PolarAlignment = new Instructions.PolarAlignment(profileService, cameraMediator, imagingMediator, fwMediator, telescopeMediator, plateSolveFactory, domeMediator, weatherDataMediator, new DummyService());

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

        private class DummyService : IWindowService {
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
    }
}