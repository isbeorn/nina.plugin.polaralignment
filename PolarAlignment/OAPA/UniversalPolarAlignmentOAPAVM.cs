using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Plugins.PolarAlignment.OAPA;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment.OAPA {
    public partial class UniversalPolarAlignmentOAPAVM : UniversalPolarAlignmentBaseVM {
        private readonly IOapaCalibrationSolver calibrationSolver;

        public UniversalPolarAlignmentOAPAVM(
            IProfileService profileService,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IPlateSolverFactory plateSolverFactory) : base(profileService) {
            calibrationSolver = new OapaPlateSolveSampler(profileService, imagingMediator, telescopeMediator, plateSolverFactory);

            // Connected and IsNotMoving live on the base VM. Their generated
            // [NotifyCanExecuteChangedFor] attributes can't reference commands declared on
            // this derived class, so re-evaluate the derived commands manually when either
            // property changes. Connected is flipped from a background Task in the base VM,
            // so marshal NotifyCanExecuteChanged onto the UI thread. The stored home is only
            // meaningful for the current controller session (the position counter restarts at
            // 0 on power-up), so it is invalidated on every connection change.
            PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(Connected) || e.PropertyName == nameof(IsNotMoving)) {
                    if (e.PropertyName == nameof(Connected)) {
                        HasHome = false;
                    }
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.CheckAccess()) {
                        NotifyDerivedCommands();
                    } else {
                        dispatcher.BeginInvoke(new Action(NotifyDerivedCommands));
                    }
                }
            };
        }

        private void NotifyDerivedCommands() {
            CalibrateGearRatiosCommand.NotifyCanExecuteChanged();
            SetHomeCommand.NotifyCanExecuteChanged();
            GoHomeCommand.NotifyCanExecuteChanged();
        }

        protected override string SystemName => "OAPA System";

        protected override IPolarAlignmentSystem CreateSystem() => new UniversalPolarAlignmentOAPA();

        public override bool DoAutomatedAdjustments {
            get => Properties.Settings.Default.DoAutomatedAdjustments;
            set {
                Properties.Settings.Default.DoAutomatedAdjustments = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override double AutomatedAdjustmentSettleTime {
            get => Properties.Settings.Default.AutomatedAdjustmentSettleTime;
            set {
                Properties.Settings.Default.AutomatedAdjustmentSettleTime = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float XGearRatio {
            get => Properties.Settings.Default.OAPAXGearRatio;
            set {
                if (value < 1) { value = 1; }
                Properties.Settings.Default.OAPAXGearRatio = value;
                if (upa != null) { upa.XGearRatio = value; }
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(PositionX));
            }
        }

        public override int XSpeed {
            get => Properties.Settings.Default.OAPAXSpeed;
            set {
                Properties.Settings.Default.OAPAXSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float YGearRatio {
            get => Properties.Settings.Default.OAPAYGearRatio;
            set {
                if (value < 1) { value = 1; }
                Properties.Settings.Default.OAPAYGearRatio = value;
                if (upa != null) { upa.YGearRatio = value; }
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(PositionY));
            }
        }

        public override int YSpeed {
            get => Properties.Settings.Default.OAPAYSpeed;
            set {
                Properties.Settings.Default.OAPAYSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool ReverseAzimuth {
            get => Properties.Settings.Default.OAPAReverseAzimuth;
            set {
                Properties.Settings.Default.OAPAReverseAzimuth = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool ReverseAltitude {
            get => Properties.Settings.Default.OAPAReverseAltitude;
            set {
                Properties.Settings.Default.OAPAReverseAltitude = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float XBacklashCompensation {
            get => Properties.Settings.Default.OAPAXBacklashCompensation;
            set {
                Properties.Settings.Default.OAPAXBacklashCompensation = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        // OAPA-specific: the altitude axis of an OAPA platform also has measurable backlash.
        // Deliberately not part of the shared VM contract - other systems do not model it.
        public float YBacklashCompensation {
            get => Properties.Settings.Default.OAPAYBacklashCompensation;
            set {
                Properties.Settings.Default.OAPAYBacklashCompensation = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        // Route the shared clearing logic to the OAPA-specific altitude compensation.
        protected override float GetBacklashCompensation(Axis axis) {
            return axis == Axis.YAxis ? YBacklashCompensation : base.GetBacklashCompensation(axis);
        }

        public int XRunCurrent {
            get => Properties.Settings.Default.OAPAXRunCurrent;
            set {
                Properties.Settings.Default.OAPAXRunCurrent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetXRunCurrent(value);
                }
            }
        }

        public int YRunCurrent {
            get => Properties.Settings.Default.OAPAYRunCurrent;
            set {
                Properties.Settings.Default.OAPAYRunCurrent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetYRunCurrent(value);
                }
            }
        }

        public int XHoldPercent {
            get => Properties.Settings.Default.OAPAXHoldPercent;
            set {
                Properties.Settings.Default.OAPAXHoldPercent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetXHoldPercent(value);
                }
            }
        }

        public int YHoldPercent {
            get => Properties.Settings.Default.OAPAYHoldPercent;
            set {
                Properties.Settings.Default.OAPAYHoldPercent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetYHoldPercent(value);
                }
            }
        }

        // ----- Home position (session-scoped) -----
        // The controller's position counter restarts at 0 on power-up, so absolute home
        // coordinates from a previous session are meaningless and potentially harmful.
        // Home therefore lives in VM state only and is invalidated on every connection change.

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GoHomeCommand))]
        private bool hasHome;

        [ObservableProperty]
        private float homeX;

        [ObservableProperty]
        private float homeY;

        public bool CanSetHome() => Connected && IsNotMoving;
        public bool CanGoHome() => Connected && IsNotMoving && HasHome;

        [RelayCommand(CanExecute = nameof(CanSetHome))]
        public void SetHome() {
            HomeX = PositionX;
            HomeY = PositionY;
            HasHome = true;
            Logger.Info($"OAPA home position set to X={HomeX:F2}, Y={HomeY:F2} (valid for this connection session)");
            Notification.ShowInformation($"Home position saved for this session (X={HomeX:F2}, Y={HomeY:F2})");
        }

        [RelayCommand(CanExecute = nameof(CanGoHome))]
        public async Task GoHome(CancellationToken token) {
            try {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = false);
                Logger.Info($"OAPA moving to home position X={HomeX:F2}, Y={HomeY:F2}");
                await upa.MoveAbsolute(Axis.XAxis, XSpeed, HomeX, token).ConfigureAwait(false);
                await upa.MoveAbsolute(Axis.YAxis, YSpeed, HomeY, token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Failed to move to home position: {ex.Message}");
            } finally {
                await Application.Current.Dispatcher.BeginInvoke(() => IsNotMoving = true);
            }
        }

        // ----- Self-Calibration -----
        // Orchestration and geometry live in OapaCalibrationService/OapaCalibrationGeometry;
        // this VM only exposes commands and observable state.

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CalibrateGearRatiosCommand))]
        private bool calibrationRunning;

        [ObservableProperty]
        private string calibrationStatus = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyCalibrationCommand))]
        [NotifyCanExecuteChangedFor(nameof(DiscardCalibrationCommand))]
        private bool hasCalibrationResult;

        [ObservableProperty]
        private float discoveredXRatio;

        [ObservableProperty]
        private float discoveredYRatio;

        [ObservableProperty]
        private float discoveredXBacklash;

        [ObservableProperty]
        private float discoveredYBacklash;

        [ObservableProperty]
        private string calibrationConsistencyMessage = string.Empty;

        public bool CanCalibrate() => Connected && IsNotMoving && !CalibrationRunning;

        private sealed class SpeedAwareMotion : IOapaCalibrationMotion {
            private readonly UniversalPolarAlignmentOAPAVM vm;
            public SpeedAwareMotion(UniversalPolarAlignmentOAPAVM vm) { this.vm = vm; }
            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                var speed = axis == Axis.XAxis ? vm.XSpeed : vm.YSpeed;
                return vm.upa.MoveRelative(axis, speed, arcmin, token);
            }
        }

        [RelayCommand(CanExecute = nameof(CanCalibrate))]
        public Task CalibrateGearRatios(CancellationToken token) {
            return Task.Run(async () => {
                try {
                    await Application.Current.Dispatcher.BeginInvoke(() => {
                        IsNotMoving = false;
                        CalibrationRunning = true;
                        HasCalibrationResult = false;
                        CalibrationStatus = "Starting calibration...";
                        CalibrationConsistencyMessage = string.Empty;
                    });

                    Logger.Info("OAPA self-calibration started");
                    var service = new OapaCalibrationService(new SpeedAwareMotion(this), calibrationSolver);
                    Action<string> reportStatus = s => Application.Current.Dispatcher.BeginInvoke(() => CalibrationStatus = s);

                    var x = await service.CalibrateAxisWithAutoReverse(
                        Axis.XAxis, XGearRatio, ReverseAzimuth, "X (Azimuth)", reportStatus, token);
                    if (x.Flipped) {
                        await Application.Current.Dispatcher.BeginInvoke(() => ReverseAzimuth = !ReverseAzimuth);
                    }
                    var y = await service.CalibrateAxisWithAutoReverse(
                        Axis.YAxis, YGearRatio, ReverseAltitude, "Y (Altitude)", reportStatus, token);
                    if (y.Flipped) {
                        await Application.Current.Dispatcher.BeginInvoke(() => ReverseAltitude = !ReverseAltitude);
                    }

                    string consistencyMsg;
                    if (x.Consistent && y.Consistent) {
                        var notes = new List<string>();
                        if (x.Flipped) { notes.Add("Reverse Az auto-corrected"); }
                        if (y.Flipped) { notes.Add("Reverse Alt auto-corrected"); }
                        consistencyMsg = notes.Count == 0
                            ? "Direction consistency: OK"
                            : "Direction consistency: OK (" + string.Join(", ", notes) + ")";
                    } else {
                        consistencyMsg = $"Direction consistency: WARNING (X={(x.Consistent ? "ok" : "fail")}, Y={(y.Consistent ? "ok" : "fail")}). Auto-flip did not resolve it; check wiring.";
                    }
                    if (x.Asymmetric || y.Asymmetric) {
                        var axes = x.Asymmetric && y.Asymmetric ? "X and Y" : (x.Asymmetric ? "X" : "Y");
                        consistencyMsg += $" \u26a0 Forward/reverse legs on {axes} differ by more than 20%: the discovered values may be unreliable. Re-run with the scope pointing at a lower-altitude, star-rich field.";
                    }

                    await Application.Current.Dispatcher.BeginInvoke(() => {
                        DiscoveredXRatio = x.Ratio;
                        DiscoveredYRatio = y.Ratio;
                        DiscoveredXBacklash = x.BacklashArcmin;
                        DiscoveredYBacklash = y.BacklashArcmin;
                        CalibrationConsistencyMessage = consistencyMsg;
                        CalibrationStatus = $"Done. X={x.Ratio:F2}, Y={y.Ratio:F2}, backlash X={x.BacklashArcmin:F2}', Y={y.BacklashArcmin:F2}'";
                        HasCalibrationResult = true;
                    });

                    Logger.Info($"OAPA calibration result: X={x.Ratio:F2}, Y={y.Ratio:F2}, backlash X={x.BacklashArcmin:F2}', Y={y.BacklashArcmin:F2}', consistency: X={x.Consistent}, Y={y.Consistent}");
                    Notification.ShowInformation(
                        $"Calibration done. X factor: {x.Ratio:F2}, Y factor: {y.Ratio:F2}, backlash X: {x.BacklashArcmin:F2}', Y: {y.BacklashArcmin:F2}'",
                        TimeSpan.FromSeconds(30));
                } catch (OperationCanceledException) {
                    Logger.Info("OAPA self-calibration cancelled");
                    await Application.Current.Dispatcher.BeginInvoke(() => CalibrationStatus = "Cancelled");
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Notification.ShowError($"Calibration failed: {ex.Message}");
                    await Application.Current.Dispatcher.BeginInvoke(() => CalibrationStatus = $"Failed: {ex.Message}");
                } finally {
                    await Application.Current.Dispatcher.BeginInvoke(() => {
                        CalibrationRunning = false;
                        IsNotMoving = true;
                    });
                }
            });
        }

        [RelayCommand(CanExecute = nameof(HasCalibrationResult))]
        public void ApplyCalibration() {
            try {
                XGearRatio = DiscoveredXRatio;
                YGearRatio = DiscoveredYRatio;
                XBacklashCompensation = DiscoveredXBacklash;
                YBacklashCompensation = DiscoveredYBacklash;
                HasCalibrationResult = false;
                CalibrationStatus = "Applied";
                Logger.Info($"OAPA calibration applied: X={DiscoveredXRatio:F2}, Y={DiscoveredYRatio:F2}, backlash X={DiscoveredXBacklash:F2}', Y={DiscoveredYBacklash:F2}'");
                Notification.ShowInformation("Calibration factors and backlash compensation updated", TimeSpan.FromSeconds(30));
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Failed to apply calibration: {ex.Message}");
            }
        }

        [RelayCommand(CanExecute = nameof(HasCalibrationResult))]
        public void DiscardCalibration() {
            DiscoveredXRatio = 0;
            DiscoveredYRatio = 0;
            DiscoveredXBacklash = 0;
            DiscoveredYBacklash = 0;
            HasCalibrationResult = false;
            CalibrationConsistencyMessage = string.Empty;
            CalibrationStatus = "Discarded";
        }
    }
}
