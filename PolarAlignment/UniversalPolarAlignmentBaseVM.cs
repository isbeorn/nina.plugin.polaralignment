using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment {
    public abstract partial class UniversalPolarAlignmentBaseVM : BaseVM, IPolarAlignmentSystemVM {
        protected IPolarAlignmentSystem upa;

        protected abstract IPolarAlignmentSystem CreateSystem();
        protected abstract string SystemName { get; }

        protected UniversalPolarAlignmentBaseVM(IProfileService profileService) : base(profileService) {
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

        public abstract bool DoAutomatedAdjustments { get; set; }
        public abstract double AutomatedAdjustmentSettleTime { get; set; }
        public abstract float XGearRatio { get; set; }
        public abstract int XSpeed { get; set; }
        public abstract float YGearRatio { get; set; }
        public abstract int YSpeed { get; set; }
        public abstract bool ReverseAzimuth { get; set; }
        public abstract bool ReverseAltitude { get; set; }
        public abstract float XBacklashCompensation { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(NudgeXCommand))]
        [NotifyCanExecuteChangedFor(nameof(NudgeYCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveXCommand))]
        [NotifyCanExecuteChangedFor(nameof(MoveYCommand))]
        private bool isNotMoving;

        private CancellationTokenSource pollCts;

        [RelayCommand]
        public Task Connect() {
            if (upa?.Connected == true) { return Task.CompletedTask; }
            return Task.Run(async () => {
                try {
                    await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);

                    upa = CreateSystem();
                    _ = StartPoll();
                    Connected = true;
                    Notification.ShowInformation($"Successfully connected to {SystemName}");
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Notification.ShowError($"Unable to connect to {SystemName}");
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
            Notification.ShowInformation($"Disconnected from {SystemName}");
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task NudgeX(float position, CancellationToken token) {
            await TryNudgeX(position, token);
        }

        public async Task<bool> TryNudgeX(float position, CancellationToken token) {
            try {
                if (ReverseAzimuth) { position = position * -1; }
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                Logger.Info($"Nudging {SystemName} along X axis by {position}");
                var lastDirection = upa.XLastDirection;
                await upa.MoveRelative(Axis.XAxis, XSpeed, position, token).ConfigureAwait(false);
                var currentDirection = upa.XLastDirection;
                await ClearBacklash(position, lastDirection, currentDirection, token);
                return true;
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
                return false;
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task NudgeY(float position, CancellationToken token) {
            await TryNudgeY(position, token);
        }

        public async Task<bool> TryNudgeY(float position, CancellationToken token) {
            try {
                if (ReverseAltitude) { position = position * -1; }
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                Logger.Info($"Nudging {SystemName} along Y axis by {position}");
                await upa.MoveRelative(Axis.YAxis, YSpeed, position, token).ConfigureAwait(false);
                return true;
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
                return false;
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

                var target = TargetPositionX;
                if (ReverseAzimuth) { target = target * -1; }

                Logger.Info($"Moving {SystemName} along X axis to {target}");
                var lastDirection = upa.XLastDirection;

                await upa.MoveAbsolute(Axis.XAxis, XSpeed, target, token).ConfigureAwait(false);
                var currentDirection = upa.XLastDirection;
                // Manual absolute moves always clear on reversal: the user expects the axis
                // to land settled, and there is no fine-approach nudge context here.
                await ClearBacklash(float.MaxValue, lastDirection, currentDirection, token);
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        private async Task ClearBacklash(float movedMagnitude, LastDirection lastDirection, LastDirection currentDirection, CancellationToken token) {
            if (BacklashCompensationPlanner.ShouldClear(movedMagnitude, XBacklashCompensation, lastDirection, currentDirection)) {
                Logger.Info("Direction changed. Clearing backlash");
                var sequence = BacklashCompensationPlanner.CreateSequence(XBacklashCompensation, currentDirection);
                await upa.MoveRelative(Axis.XAxis, XSpeed, sequence.FirstMove, token).ConfigureAwait(false);
                await upa.MoveRelative(Axis.XAxis, XSpeed, sequence.SecondMove, token).ConfigureAwait(false);
            } else if (lastDirection != currentDirection && Math.Abs(XBacklashCompensation) > 0) {
                Logger.Info($"Direction changed but commanded move ({movedMagnitude:F2}) is smaller than the backlash compensation ({XBacklashCompensation:F2}); skipping backlash clearing");
            }
        }

        /// <summary>
        /// The base policy keeps the legacy conservative controller profile; systems whose
        /// hardware tolerates faster convergence (OAPA) opt into the aggressive one.
        /// </summary>
        public virtual bool AggressiveCorrectionProfile => false;

        /// <summary>
        /// Default correction-limit capability: the controller's stock per-cycle bound.
        /// Systems with a specific policy (e.g. OAPA error-proportional scaling) override this.
        /// </summary>
        public virtual double GetMaximumCorrectionMagnitude(double currentTotalErrorArcmin) {
            return AutomatedAdjustmentController.DefaultMaximumMoveMagnitude;
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveY(CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                var target = TargetPositionY;
                if (ReverseAltitude) { target = target * -1; }

                Logger.Info($"Moving {SystemName} along Y axis to {target}");
                await upa.MoveAbsolute(Axis.YAxis, YSpeed, target, token).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
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
                    if (IsNotMoving) {
                        await upa.RefreshStatus(token);
                    }
                    await Application.Current.Dispatcher.BeginInvoke(UpdatePositions);
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private void UpdatePositions() {
            if (upa == null) { return; }
            PositionX = upa.XPosition1;
            PositionY = upa.YPosition1;
        }
    }
}
