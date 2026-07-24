using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Plugins.PolarAlignment.OAPA;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment.OAPA {
    public partial class UniversalPolarAlignmentOAPAVM : UniversalPolarAlignmentBaseVM {
        private readonly IProfileService profileService;
        private readonly IImagingMediator imagingMediator;
        private readonly ITelescopeMediator telescopeMediator;
        private readonly IPlateSolverFactory plateSolverFactory;

        // Calibration tunables (constants for v1; promote to settings if needed)
        // Large lever arm: plate-solve noise (2-5") and backlash contaminate short moves.
        // A 45' leg keeps the relative measurement error well below 1%.
        private const float CalibrationStepArcmin = 45.0f;
        private const int CalibrationPlateSolveRetries = 2;
        private const double CalibrationSearchRadiusDeg = 30.0;

        public UniversalPolarAlignmentOAPAVM(
            IProfileService profileService,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IPlateSolverFactory plateSolverFactory) : base(profileService) {
            this.profileService = profileService;
            this.imagingMediator = imagingMediator;
            this.telescopeMediator = telescopeMediator;
            this.plateSolverFactory = plateSolverFactory;

            // Connected and IsNotMoving live on the base VM. Their generated
            // [NotifyCanExecuteChangedFor] attributes can't reference commands declared on
            // this derived class, so re-evaluate CalibrateGearRatiosCommand manually when
            // either property changes. Connected is flipped from a background Task in the base
            // VM, so marshal NotifyCanExecuteChanged onto the UI thread.
            PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(Connected) || e.PropertyName == nameof(IsNotMoving)) {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.CheckAccess()) {
                        CalibrateGearRatiosCommand.NotifyCanExecuteChanged();
                        SetHomeCommand.NotifyCanExecuteChanged();
                        GoHomeCommand.NotifyCanExecuteChanged();
                    } else {
                        dispatcher.BeginInvoke(new Action(() => {
                            CalibrateGearRatiosCommand.NotifyCanExecuteChanged();
                            SetHomeCommand.NotifyCanExecuteChanged();
                            GoHomeCommand.NotifyCanExecuteChanged();
                        }));
                    }
                }
            };
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

        public override float YBacklashCompensation {
            get => Properties.Settings.Default.OAPAYBacklashCompensation;
            set {
                Properties.Settings.Default.OAPAYBacklashCompensation = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
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

        // ----- Home position (plugin-side, firmware-agnostic) -----
        // Stored in plugin settings rather than firmware so it works with any OAPA-compatible
        // controller. Positions are in the same axis units shown in the control panel; note the
        // controller's counter restarts at 0 on power-up, so the stored home is valid for the
        // current power session (or as long as the platform is not moved while unpowered).

        public float HomeX {
            get => Properties.Settings.Default.OAPAHomeX;
            set {
                Properties.Settings.Default.OAPAHomeX = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public float HomeY {
            get => Properties.Settings.Default.OAPAHomeY;
            set {
                Properties.Settings.Default.OAPAHomeY = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool CanUseHome() => Connected && IsNotMoving;

        [RelayCommand(CanExecute = nameof(CanUseHome))]
        public void SetHome() {
            HomeX = PositionX;
            HomeY = PositionY;
            Logger.Info($"OAPA home position set to X={HomeX:F2}, Y={HomeY:F2}");
            Notification.ShowInformation($"Home position saved (X={HomeX:F2}, Y={HomeY:F2})");
        }

        [RelayCommand(CanExecute = nameof(CanUseHome))]
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

                    var (xRatio, xBacklash, xConsistent, xAsymmetric, xFlipped) = await CalibrateAxisWithAutoReverseAsync(
                        Axis.XAxis, XSpeed, XGearRatio,
                        () => ReverseAzimuth, v => ReverseAzimuth = v,
                        "X (Azimuth)", token);
                    var (yRatio, yBacklash, yConsistent, yAsymmetric, yFlipped) = await CalibrateAxisWithAutoReverseAsync(
                        Axis.YAxis, YSpeed, YGearRatio,
                        () => ReverseAltitude, v => ReverseAltitude = v,
                        "Y (Altitude)", token);

                    string consistencyMsg;
                    if (xConsistent && yConsistent) {
                        var notes = new List<string>();
                        if (xFlipped) { notes.Add("Reverse Az auto-corrected"); }
                        if (yFlipped) { notes.Add("Reverse Alt auto-corrected"); }
                        consistencyMsg = notes.Count == 0
                            ? "Direction consistency: OK"
                            : "Direction consistency: OK (" + string.Join(", ", notes) + ")";
                    } else {
                        consistencyMsg = $"Direction consistency: WARNING (X={(xConsistent ? "ok" : "fail")}, Y={(yConsistent ? "ok" : "fail")}). Auto-flip did not resolve it; check wiring.";
                    }
                    if (xAsymmetric || yAsymmetric) {
                        var axes = xAsymmetric && yAsymmetric ? "X and Y" : (xAsymmetric ? "X" : "Y");
                        consistencyMsg += $" ⚠ Forward/reverse legs on {axes} differ by more than 20%: the discovered values may be unreliable. Re-run with the scope pointing at a lower-altitude, star-rich field.";
                    }

                    await Application.Current.Dispatcher.BeginInvoke(() => {
                        DiscoveredXRatio = xRatio;
                        DiscoveredYRatio = yRatio;
                        DiscoveredXBacklash = xBacklash;
                        DiscoveredYBacklash = yBacklash;
                        CalibrationConsistencyMessage = consistencyMsg;
                        CalibrationStatus = $"Done. X={xRatio:F2}, Y={yRatio:F2}, backlash X={xBacklash:F2}', Y={yBacklash:F2}'";
                        HasCalibrationResult = true;
                    });

                    Logger.Info($"OAPA calibration result: X={xRatio:F2}, Y={yRatio:F2}, backlash X={xBacklash:F2}', Y={yBacklash:F2}', consistency: X={xConsistent}, Y={yConsistent}");
                    Notification.ShowInformation(
                        $"Calibration done. X factor: {xRatio:F2}, Y factor: {yRatio:F2}, backlash X: {xBacklash:F2}', Y: {yBacklash:F2}'",
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

        /// <summary>
        /// Calibrate an axis. If the first pass shows direction inconsistency, flip the
        /// caller's Reverse flag and retry once. If the retry passes, persist the flip and
        /// return the corrected ratio. If it still fails, restore the original flag and
        /// surface the inconsistent first-pass result.
        /// </summary>
        private async Task<(float ratio, float backlashArcmin, bool consistent, bool asymmetric, bool flipped)> CalibrateAxisWithAutoReverseAsync(
            Axis axis, int speed, float currentRatio,
            Func<bool> getReverse, Action<bool> setReverse,
            string axisLabel, CancellationToken token) {

            bool originalReverse = getReverse();
            var (ratio, backlash, consistent, asymmetric) = await CalibrateAxisAsync(axis, speed, currentRatio, originalReverse, axisLabel, token);
            if (consistent) { return (ratio, backlash, true, asymmetric, false); }

            Logger.Info($"OAPA cal {axisLabel}: direction inconsistent, retrying with Reverse flipped ({originalReverse} -> {!originalReverse})");
            await SetStatusAsync($"{axisLabel}: auto-flipping Reverse and retrying...");

            var (ratio2, backlash2, consistent2, asymmetric2) = await CalibrateAxisAsync(axis, speed, currentRatio, !originalReverse, axisLabel, token);
            if (consistent2) {
                await Application.Current.Dispatcher.BeginInvoke(() => setReverse(!originalReverse));
                Logger.Info($"OAPA cal {axisLabel}: auto-flip succeeded, persisted Reverse={!originalReverse}, ratio={ratio2:F2}");
                return (ratio2, backlash2, true, asymmetric2, true);
            }

            Logger.Warning($"OAPA cal {axisLabel}: auto-flip did not resolve inconsistency; keeping original Reverse={originalReverse}");
            return (ratio, backlash, false, asymmetric, false);
        }

        /// <summary>
        /// Large-lever calibration with backlash measurement.
        /// Sequence: baseline solve (fail-fast before any motion), prime +S (absorbs any pending
        /// backlash), solve A, +S, solve B, -S, solve C, -S, solve D.
        /// The A->B (forward) and C->D (reverse) legs are single-direction and therefore backlash-free,
        /// yielding the gear ratio. The B->C reversal leg comes up short by exactly the backlash amount.
        /// Net commanded motion is zero, so the axis ends where it started; on mid-sequence failure the
        /// accumulated commanded motion is driven back before rethrowing.
        /// Azimuth sky displacements are divided by cos(field altitude): a base rotation of θ in azimuth
        /// moves a field at altitude h by only θ·cos(h), so uncorrected factors would depend on where
        /// the scope happens to point.
        /// </summary>
        private async Task<(float ratio, float backlashArcmin, bool consistent, bool asymmetric)> CalibrateAxisAsync(
            Axis axis, int speed, float currentRatio, bool reversed, string axisLabel, CancellationToken token) {

            float commanded = CalibrationStepArcmin;
            float step = reversed ? -commanded : commanded;

            // Fail fast on an unsolvable field before commanding any motion.
            await SetStatusAsync($"{axisLabel}: baseline solve...");
            var baseline = await CaptureAndSolveWithRetryAsync(token);

            if (axis == Axis.XAxis) {
                var baselineAlt = FieldAltitudeDegrees(baseline);
                if (Math.Cos(baselineAlt * Math.PI / 180.0) < MinimumAzimuthCosAltitude) {
                    throw new InvalidOperationException(
                        $"{axisLabel}: field altitude {baselineAlt:F0}\u00b0 is too close to the zenith for azimuth calibration. " +
                        "Point the scope at a lower altitude (ideally toward the celestial pole) and retry.");
                }
            }

            float movedArcmin = 0f;
            try {
                await SetStatusAsync($"{axisLabel}: priming +{commanded:F0}'...");
                await upa.MoveRelative(axis, speed, step, token).ConfigureAwait(false);
                movedArcmin += step;
                var solveA = await CaptureAndSolveWithRetryAsync(token);
                token.ThrowIfCancellationRequested();

                await SetStatusAsync($"{axisLabel}: forward leg +{commanded:F0}'...");
                await upa.MoveRelative(axis, speed, step, token).ConfigureAwait(false);
                movedArcmin += step;
                var solveB = await CaptureAndSolveWithRetryAsync(token);
                token.ThrowIfCancellationRequested();

                await SetStatusAsync($"{axisLabel}: reversal leg -{commanded:F0}'...");
                await upa.MoveRelative(axis, speed, -step, token).ConfigureAwait(false);
                movedArcmin -= step;
                var solveC = await CaptureAndSolveWithRetryAsync(token);
                token.ThrowIfCancellationRequested();

                await SetStatusAsync($"{axisLabel}: reverse leg -{commanded:F0}'...");
                await upa.MoveRelative(axis, speed, -step, token).ConfigureAwait(false);
                movedArcmin -= step;
                var solveD = await CaptureAndSolveWithRetryAsync(token);

                var forwardArcmin = AxisDisplacementArcmin(axis, solveA, solveB);
                var reversalArcmin = AxisDisplacementArcmin(axis, solveB, solveC);
                var reverseArcmin = AxisDisplacementArcmin(axis, solveC, solveD);

                if (forwardArcmin < 0.1 || reverseArcmin < 0.1) {
                    throw new InvalidOperationException($"{axisLabel}: axis did not move measurably; check clutch and motor current");
                }

                var cleanArcmin = (forwardArcmin + reverseArcmin) / 2.0;
                float observedRatio = (float)(currentRatio * (commanded / cleanArcmin));

                // The reversal leg lost this much commanded motion to backlash.
                var backlash = (float)(commanded * (1.0 - reversalArcmin / cleanArcmin));
                if (backlash < 0f) {
                    backlash = 0f;
                } else if (backlash > commanded / 2f) {
                    Logger.Warning($"OAPA cal {axisLabel}: measured backlash {backlash:F2}' exceeds half the calibration step; clamping. Check for mechanical slippage.");
                    backlash = commanded / 2f;
                }

                // The two clean legs measure the same physical motion; a large mismatch means the
                // measurement itself is unreliable (field drift, flexure, slipping mechanics).
                var asymmetry = Math.Abs(forwardArcmin - reverseArcmin) / Math.Max(forwardArcmin, reverseArcmin);
                bool asymmetric = asymmetry > 0.20;
                if (asymmetric) {
                    Logger.Warning($"OAPA cal {axisLabel}: forward ({forwardArcmin:F2}') and reverse ({reverseArcmin:F2}') legs differ by {asymmetry:P0}; discovered values may be unreliable");
                }

                // Direction consistency: forward and reversal legs must be antiparallel on the tangent plane.
                bool consistent = TangentDotProduct(solveA, solveB, solveB, solveC) < 0;

                Logger.Info($"OAPA cal {axisLabel}: forward={forwardArcmin:F2}', reversal={reversalArcmin:F2}', reverse={reverseArcmin:F2}', ratio={observedRatio:F2}, backlash={backlash:F2}', consistent={consistent}, asymmetric={asymmetric}");
                return (observedRatio, backlash, consistent, asymmetric);
            } catch (Exception) when (movedArcmin != 0f) {
                // Best-effort: drive the axis back to its starting position before surfacing the error.
                Logger.Info($"OAPA cal {axisLabel}: failure with {movedArcmin:F1}' of commanded motion outstanding; driving back");
                try {
                    await upa.MoveRelative(axis, speed, -movedArcmin, CancellationToken.None).ConfigureAwait(false);
                } catch (Exception restoreEx) {
                    Logger.Error($"OAPA cal {axisLabel}: failed to restore start position", restoreEx);
                }
                throw;
            }
        }

        // Azimuth calibration degenerates as cos(alt) -> 0; below this the lever is too foreshortened.
        private const double MinimumAzimuthCosAltitude = 0.25;

        /// <summary>
        /// Converts a measured sky displacement into axis displacement. For the azimuth axis the
        /// sky motion is foreshortened by cos(altitude) of the observed field; the altitude axis
        /// transfers 1:1.
        /// </summary>
        private double AxisDisplacementArcmin(Axis axis, PlateSolveResult from, PlateSolveResult to) {
            var skyArcmin = AngularSeparationDegrees(from, to) * 60.0;
            if (axis != Axis.XAxis) { return skyArcmin; }

            var meanAlt = (FieldAltitudeDegrees(from) + FieldAltitudeDegrees(to)) / 2.0;
            var cosAlt = Math.Cos(meanAlt * Math.PI / 180.0);
            if (cosAlt < MinimumAzimuthCosAltitude) {
                throw new InvalidOperationException(
                    $"Field altitude {meanAlt:F0}\u00b0 is too close to the zenith for azimuth calibration. " +
                    "Point the scope at a lower altitude (ideally toward the celestial pole) and retry.");
            }
            return skyArcmin / cosAlt;
        }

        private double FieldAltitudeDegrees(PlateSolveResult solve) {
            var latitude = Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Latitude);
            var longitude = Angle.ByDegree(profileService.ActiveProfile.AstrometrySettings.Longitude);
            var topocentric = solve.Coordinates.Transform(latitude, longitude, solve.Coordinates.DateTime.Now);
            return topocentric.Altitude.Degree;
        }

        private async Task SetStatusAsync(string status) {
            await Application.Current.Dispatcher.BeginInvoke(() => CalibrationStatus = status);
        }

        private static double AngularSeparationDegrees(PlateSolveResult a, PlateSolveResult b) {
            // Great-circle separation between two RA/Dec points (degrees)
            double ra1 = a.Coordinates.RADegrees * Math.PI / 180.0;
            double ra2 = b.Coordinates.RADegrees * Math.PI / 180.0;
            double dec1 = a.Coordinates.Dec * Math.PI / 180.0;
            double dec2 = b.Coordinates.Dec * Math.PI / 180.0;
            double cosSep = Math.Sin(dec1) * Math.Sin(dec2) + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
            cosSep = Math.Max(-1.0, Math.Min(1.0, cosSep));
            return Math.Acos(cosSep) * 180.0 / Math.PI;
        }

        private static double TangentDotProduct(PlateSolveResult a1, PlateSolveResult a2, PlateSolveResult b1, PlateSolveResult b2) {
            // Dot product of displacement vectors a1->a2 and b1->b2 projected on the
            // tangent plane (RA*cos(dec), Dec) around a1.
            double cosDec = Math.Cos(a1.Coordinates.Dec * Math.PI / 180.0);
            double vxA = (a2.Coordinates.RADegrees - a1.Coordinates.RADegrees) * cosDec;
            double vyA = a2.Coordinates.Dec - a1.Coordinates.Dec;
            double vxB = (b2.Coordinates.RADegrees - b1.Coordinates.RADegrees) * cosDec;
            double vyB = b2.Coordinates.Dec - b1.Coordinates.Dec;
            return vxA * vxB + vyA * vyB;
        }

        private async Task<PlateSolveResult> CaptureAndSolveWithRetryAsync(CancellationToken token) {
            Exception lastException = null;
            for (int attempt = 0; attempt <= CalibrationPlateSolveRetries; attempt++) {
                token.ThrowIfCancellationRequested();
                try {
                    var result = await CaptureAndSolveOnceAsync(token).ConfigureAwait(false);
                    if (result != null && result.Success) { return result; }
                    Logger.Warning($"Plate solve unsuccessful (attempt {attempt + 1}/{CalibrationPlateSolveRetries + 1})");
                } catch (OperationCanceledException) { throw; } catch (Exception ex) {
                    lastException = ex;
                    Logger.Warning($"Plate solve attempt {attempt + 1} failed: {ex.Message}");
                }
            }
            throw new InvalidOperationException(
                $"Plate solve failed after {CalibrationPlateSolveRetries + 1} attempts" +
                (lastException != null ? $": {lastException.Message}" : string.Empty));
        }

        private async Task<PlateSolveResult> CaptureAndSolveOnceAsync(CancellationToken token) {
            var pss = profileService.ActiveProfile.PlateSolveSettings;
            var seq = new CaptureSequence() {
                Binning = new BinningMode(pss.Binning, pss.Binning),
                Gain = pss.Gain,
                ExposureTime = pss.ExposureTime,
                Offset = -1,
                FilterType = pss.Filter,
                ImageType = CaptureSequence.ImageTypes.SNAPSHOT
            };

            IRenderedImage image = await imagingMediator.CaptureAndPrepareImage(
                seq, new PrepareImageParameters(true, false), token, null);
            if (image == null) { return null; }

            var solver = plateSolverFactory.GetPlateSolver(pss);
            var imageSolver = plateSolverFactory.GetImageSolver(solver, null);
            var parameter = new PlateSolveParameter() {
                Binning = pss.Binning,
                Coordinates = telescopeMediator.GetCurrentPosition(),
                DownSampleFactor = pss.DownSampleFactor,
                FocalLength = profileService.ActiveProfile.TelescopeSettings.FocalLength,
                MaxObjects = pss.MaxObjects,
                PixelSize = profileService.ActiveProfile.CameraSettings.PixelSize,
                Regions = pss.Regions,
                SearchRadius = CalibrationSearchRadiusDeg,
                DisableNotifications = true
            };
            return await imageSolver.Solve(image.RawImageData, parameter, null, token).ConfigureAwait(false);
        }
    }
}
