using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment.Avalon {
    public partial class UniversalPolarAlignmentVM : BaseVM {
        private UniversalPolarAlignment upa;

        public UniversalPolarAlignmentVM(IProfileService profileService) : base(profileService) {
            IsNotMoving = true;
        }

        [ObservableProperty]
        private bool connected;

        [ObservableProperty]
        private float positionX;

        [ObservableProperty]
        private float positionY;

        [ObservableProperty]
        private float targetPositionX;

        [ObservableProperty]
        private float targetPositionY;

        public float XGearRatio {
            get {
                return Properties.Settings.Default.AvalonXGearRatio;
            }
            set {
                if (value < 1) { value = 1; }
                Properties.Settings.Default.AvalonXGearRatio = value;
                upa.XGearRatio = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(PositionX));
            }
        }

        public int XSpeed {
            get {
                return Properties.Settings.Default.AvalonXSpeed;
            }
            set {
                Properties.Settings.Default.AvalonXSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public float YGearRatio {
            get {
                return Properties.Settings.Default.AvalonYGearRatio;
            }
            set {
                if(value < 1) { value = 1; }
                Properties.Settings.Default.AvalonYGearRatio = value;
                upa.YGearRatio = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(PositionY));
            }
        }

        public int YSpeed {
            get {
                return Properties.Settings.Default.AvalonYSpeed;
            }
            set {
                Properties.Settings.Default.AvalonYSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(NudgeXCommand))]
        [NotifyCanExecuteChangedFor(nameof(NudgeYCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveXCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveYCommand))]
        private bool isNotMoving;

        private CancellationTokenSource pollCts;

        [RelayCommand]
        public Task Connect() {
            return Task.Run(async () => {
                try {
                    await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);

                    upa = new UniversalPolarAlignment();
                    _ = StartPoll();
                    Connected = true;
                    Notification.ShowInformation("Successfully connected to Avalon Polar Alignment System");
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Notification.ShowError("Unable to connect to Avalon Polar Alignment System");
                }
            });
        }

        [RelayCommand]
        public void Disconnect() {
            Connected = false;
            try {
                pollCts?.Cancel();
                upa.Dispose();                
            } catch (Exception ex) {
                Logger.Error(ex);
            }
            Notification.ShowInformation("Disconnected from Avalon Polar Alignment System");
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task NudgeX(float position, CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                await upa.MoveRelative(UniversalPolarAlignment.Axis.XAxis, XSpeed, position, token).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task NudgeY(float position, CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                await upa.MoveRelative(UniversalPolarAlignment.Axis.YAxis, YSpeed, position, token).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveX(CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                await upa.MoveAbsolute(UniversalPolarAlignment.Axis.XAxis, XSpeed, TargetPositionX, token).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveY(CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                await upa.MoveAbsolute(UniversalPolarAlignment.Axis.YAxis, YSpeed, TargetPositionY, token).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        private async Task StartPoll() {
            pollCts = new CancellationTokenSource();
            var token = pollCts.Token;
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            try {
                while (await timer.WaitForNextTickAsync(token) && !token.IsCancellationRequested) {
                    PositionX = upa.XPosition1;
                    PositionY = upa.YPosition1;
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }
    }
}
