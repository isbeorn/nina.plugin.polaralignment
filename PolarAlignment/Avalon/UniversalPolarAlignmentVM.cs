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
using System.Windows.Media.Animation;
using static NINA.Plugins.PolarAlignment.Avalon.UniversalPolarAlignment;

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

        public bool UsePolarAlignmentSystem {
            get {
                return Properties.Settings.Default.UseAvalonPolarAlignmentSystem;
            }
            set {
                Properties.Settings.Default.UseAvalonPolarAlignmentSystem = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool DoAutomatedAdjustments {
            get {
                return Properties.Settings.Default.DoAutomatedAdjustments;
            }
            set {
                Properties.Settings.Default.DoAutomatedAdjustments = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double AutomatedAdjustmentSettleTime {
            get {
                return Properties.Settings.Default.AutomatedAdjustmentSettleTime;
            }
            set {
                Properties.Settings.Default.AutomatedAdjustmentSettleTime = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }
        
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

        public float XBacklashCompensation {
            get {
                return Properties.Settings.Default.AvalonXBacklashCompensation;
            }
            set {
                Properties.Settings.Default.AvalonXBacklashCompensation = value;
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
            if(upa?.Connected == true) { return Task.CompletedTask; }
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
            if (upa?.Connected != true) { return; }
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

                Logger.Info($"Nudging UPA along X axis by {position}");
                var lastDirection = upa.XLastDirection;
                await upa.MoveRelative(UniversalPolarAlignment.Axis.XAxis, XSpeed, position, token).ConfigureAwait(false);
                var currentDirection = upa.XLastDirection;
                await ClearBacklash(lastDirection, currentDirection, token);
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

                Logger.Info($"Nudging UPA along Y axis by {position}");
                await upa.MoveRelative(UniversalPolarAlignment.Axis.YAxis, YSpeed, position, token).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        public new void RaiseAllPropertiesChanged() {
            base.RaiseAllPropertiesChanged();
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveX(CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                Logger.Info($"Moving UPA along X axis to {TargetPositionX}");
                var lastDirection = upa.XLastDirection;
                await upa.MoveAbsolute(UniversalPolarAlignment.Axis.XAxis, XSpeed, TargetPositionX, token).ConfigureAwait(false);
                var currentDirection = upa.XLastDirection;
                await ClearBacklash(lastDirection, currentDirection, token);
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        private async Task ClearBacklash(LastDirection lastDirection, LastDirection currentDirection, CancellationToken token) {
            if (lastDirection != currentDirection) {
                if (Math.Abs(XBacklashCompensation) > 0) {
                    Logger.Info("Direction changed. Clearing backlash");
                    await upa.MoveRelative(UniversalPolarAlignment.Axis.XAxis, XSpeed, -XBacklashCompensation, token).ConfigureAwait(false);
                    await upa.MoveRelative(UniversalPolarAlignment.Axis.XAxis, XSpeed, XBacklashCompensation, token).ConfigureAwait(false);
                }
            } 
        }


        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveY(CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                Logger.Info($"Moving UPA along Y axis to {TargetPositionY}");
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
