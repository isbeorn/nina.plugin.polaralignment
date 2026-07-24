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
        public abstract float YBacklashCompensation { get; set; }

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
                await ClearBacklash(Axis.XAxis, XSpeed, XBacklashCompensation, position, lastDirection, currentDirection, token);
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
                var lastDirection = upa.YLastDirection;
                await upa.MoveRelative(Axis.YAxis, YSpeed, position, token).ConfigureAwait(false);
                var currentDirection = upa.YLastDirection;
                await ClearBacklash(Axis.YAxis, YSpeed, YBacklashCompensation, position, lastDirection, currentDirection, token);
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
                // Manual absolute moves always clear on reversal; the fine-approach guard is
                // only meant for small automated nudges.
                await ClearBacklash(Axis.XAxis, XSpeed, XBacklashCompensation, float.MaxValue, lastDirection, currentDirection, token);
            } catch (Exception ex) {
                Logger.Error(ex);
                if (ex is TimeoutException) {
                    Notification.ShowError($"Movement timeout: {ex.Message}");
                }
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        private async Task ClearBacklash(Axis axis, int speed, float compensation, float movedMagnitude, LastDirection lastDirection, LastDirection currentDirection, CancellationToken token) {
            if (lastDirection != currentDirection) {
                if (Math.Abs(compensation) > 0) {
                    // Fine-approach guard: when the commanded move is smaller than the backlash
                    // compensation itself, the out-and-back clearing excursion is far larger than
                    // the intended motion and injects more error than it removes (field-observed
                    // as error oscillation near the pole). Let the adaptive controller handle the
                    // slop implicitly instead.
                    if (Math.Abs(movedMagnitude) < Math.Abs(compensation)) {
                        Logger.Info($"Direction changed on {axis} but commanded move ({movedMagnitude:F2}) is smaller than the backlash compensation ({compensation:F2}); skipping backlash clearing");
                        return;
                    }
                    Logger.Info($"Direction changed on {axis}. Clearing backlash");
                    var sequence = BacklashCompensationPlanner.CreateSequence(compensation, currentDirection);
                    await upa.MoveRelative(axis, speed, sequence.FirstMove, token).ConfigureAwait(false);
                    await upa.MoveRelative(axis, speed, sequence.SecondMove, token).ConfigureAwait(false);
                }
            }
        }

        [RelayCommand(CanExecute = (nameof(IsNotMoving)))]
        public async Task MoveY(CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);

                var target = TargetPositionY;
                if (ReverseAltitude) { target = target * -1; }

                Logger.Info($"Moving {SystemName} along Y axis to {target}");
                var lastDirection = upa.YLastDirection;
                await upa.MoveAbsolute(Axis.YAxis, YSpeed, target, token).ConfigureAwait(false);
                var currentDirection = upa.YLastDirection;
                await ClearBacklash(Axis.YAxis, YSpeed, YBacklashCompensation, float.MaxValue, lastDirection, currentDirection, token);
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
