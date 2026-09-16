using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Plugins.PolarAlignment.OAPA;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment.OAPA {
    public partial class UniversalPolarAlignmentOAPAVM : UniversalPolarAlignmentBaseVM {
        /// <summary>The plate-solve boundary the calibration measures through.</summary>
        protected IOapaCalibrationSolver calibrationSolver;
        private readonly ICameraMediator cameraMediator;
        private readonly CameraBlockToken cameraBlockToken;

        public UniversalPolarAlignmentOAPAVM(
            IProfileService profileService,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IPlateSolverFactory plateSolverFactory,
            ICameraMediator cameraMediator) : base(profileService) {
            // Before any bound property reads a persisted parameter: the panel is often
            // opened long before the controller is connected.
            OapaSettingsMigration.EnsureCurrent();
            calibrationSolver = new OapaPlateSolveSampler(profileService, imagingMediator, telescopeMediator, plateSolverFactory);
            this.cameraMediator = cameraMediator;

            // The camera reports to its consumers on every update. That is the only notice this
            // panel gets that another consumer - a halted alignment being stopped - has released
            // it, so the Calibrate button and its reason are re-evaluated on each one.
            cameraBlockToken = new CameraBlockToken(RefreshDerivedCommands);
            cameraMediator?.RegisterConsumer(cameraBlockToken);

            // Connected and IsNotMoving live on the base VM. Their generated
            // [NotifyCanExecuteChangedFor] attributes can't reference commands declared on
            // this derived class, so re-evaluate the derived commands manually when either
            // property changes. The stored home is only meaningful for the current controller
            // session (the position counter restarts at 0 on power-up), so it is invalidated on
            // every connection change.
            PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(Connected) || e.PropertyName == nameof(IsNotMoving)) {
                    if (e.PropertyName == nameof(Connected)) {
                        HasHome = false;
                    }
                    RefreshDerivedCommands();
                }
            };
        }

        /// <summary>
        /// Re-evaluates the derived commands on the UI thread. Connected is flipped from a
        /// background task in the base VM and camera updates arrive from the camera's own
        /// thread, so both are marshalled.
        /// </summary>
        private void RefreshDerivedCommands() {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) {
                NotifyDerivedCommands();
            } else {
                dispatcher.BeginInvoke(new Action(NotifyDerivedCommands));
            }
        }

        private void NotifyDerivedCommands() {
            RaisePropertyChanged(nameof(CalibrateUnavailableReason));
            CalibrateGearRatiosCommand.NotifyCanExecuteChanged();
            ApplyCalibrationCommand.NotifyCanExecuteChanged();
            SetHomeCommand.NotifyCanExecuteChanged();
            GoHomeCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Applies a UI-bound state change on the dispatcher when there is one. A headless
        /// host has no application, and the calibration and home commands still have
        /// to clear their running state on the way out rather than throw there.
        /// </summary>
        private static async Task RunOnUi(Action action) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null) {
                await dispatcher.BeginInvoke(action);
            } else {
                action();
            }
        }

        protected override string SystemName => "OAPA System";

        protected override IPolarAlignmentSystem CreateSystem() => new UniversalPolarAlignmentOAPA();

        /// <summary>
        /// Per-axis backlash handling mode; sole owner of the persisted setting. An
        /// unrecognised stored value falls back to Full, the single-move compensation.
        /// </summary>
        public OapaBacklashMode XBacklashMode {
            get => ParseMode(Properties.Settings.Default.OAPAXBacklashMode);
            set {
                Properties.Settings.Default.OAPAXBacklashMode = value.ToString();
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(XBacklashModeName));
            }
        }

        public OapaBacklashMode YBacklashMode {
            get => ParseMode(Properties.Settings.Default.OAPAYBacklashMode);
            set {
                Properties.Settings.Default.OAPAYBacklashMode = value.ToString();
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(YBacklashModeName));
            }
        }

        private static OapaBacklashMode ParseMode(string stored) =>
            Enum.TryParse<OapaBacklashMode>(stored, out var mode) ? mode : OapaBacklashMode.Full;

        private OapaBacklashMode BacklashModeOf(Axis axis) => axis == Axis.XAxis ? XBacklashMode : YBacklashMode;

        // String adapters for the XAML ComboBoxes.
        public string[] BacklashModeNames => Enum.GetNames(typeof(OapaBacklashMode));

        public string XBacklashModeName {
            get => XBacklashMode.ToString();
            set { if (Enum.TryParse<OapaBacklashMode>(value, out var mode)) { XBacklashMode = mode; } }
        }

        public string YBacklashModeName {
            get => YBacklashMode.ToString();
            set { if (Enum.TryParse<OapaBacklashMode>(value, out var mode)) { YBacklashMode = mode; } }
        }

        /// <summary>
        /// Route the shared clearing to the OAPA per-axis compensation, and honour the mode
        /// while doing it. Off has to mean off on every path that moves the axis: the
        /// absolute moves go through the shared clearing, which only asks for a value, so an
        /// axis set to Off would otherwise still pay two extra moves after every "move to".
        /// </summary>
        protected override float GetBacklashCompensation(Axis axis) {
            if (BacklashModeOf(axis) == OapaBacklashMode.Off) { return 0f; }
            return axis == Axis.YAxis ? YBacklashCompensation : base.GetBacklashCompensation(axis);
        }

        private float GetBacklashCompensationNegative(Axis axis) {
            if (BacklashModeOf(axis) == OapaBacklashMode.Off) { return 0f; }
            return axis == Axis.YAxis ? YBacklashCompensationNegative : XBacklashCompensationNegative;
        }

        /// <summary>
        /// OAPA relative moves replace the clear-after-move excursion with the per-axis
        /// backlash-mode plan: the compensation is folded into the move itself (Full/Soft) or
        /// the target is approached from the engaged direction only (Unidirectional). Serves
        /// both the manual and the automated fine-approach path.
        /// </summary>
        protected override async Task ExecuteRelativeMove(Axis axis, int speed, float position, CancellationToken token) {
            var mode = BacklashModeOf(axis);
            var plan = BacklashModePlanner.PlanMoves(mode, position,
                GetBacklashCompensation(axis),
                GetBacklashCompensationNegative(axis),
                LastDirectionOf(axis),
                MinimumHonourableReversal(axis));
            if (plan.Length == 0) {
                // The request is finer than this axis can be positioned: its own backlash
                // compensation would inject a larger error than the move is trying to remove.
                // Reported rather than silently skipped - the correction loop will read the
                // same error again, and the log has to explain why nothing moved.
                Logger.Info($"OAPA backlash mode {mode} on {axis}: {position:F2}' not commanded - a reversal below " +
                    $"{MinimumHonourableReversal(axis):F2}' is finer than this axis's compensation was measured to; " +
                    "moving would add more error than it removes");
                return;
            }
            if (plan.Length > 1 || System.Math.Abs(plan[0] - position) > float.Epsilon) {
                // The net is what the axis travels if it loses no play at all, so it is the
                // floor of what a two-leg plan can achieve. When it drifts away from the
                // requested move - or flips sign - the configured pair is asking the
                // mechanism for play it does not have, which is invisible from the legs alone.
                var net = 0f;
                foreach (var m in plan) { net += m; }
                Logger.Info($"OAPA backlash mode {mode} on {axis}: move {position:F2}' planned as [{string.Join(", ", plan)}] (net {net:F2}')");
            }
            foreach (var move in plan) {
                await upa.MoveRelative(axis, speed, move, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Smallest reversal this axis is asked to make. A compensated reversal lands where it
        /// was sent only in so far as the configured play matches the real play, and that is
        /// only known to the precision the calibration measured it with: the same detection
        /// threshold the sequence used to decide what counted as motion at all. Asking for
        /// less means the compensation's own error exceeds the correction being attempted.
        ///
        /// Zero until a calibration has run, and zero for an axis with no compensation - those
        /// pay no play, so they stay as fine as the solver allows.
        /// </summary>
        private float MinimumHonourableReversal(Axis axis) {
            if (BacklashModeOf(axis) == OapaBacklashMode.Off) { return 0f; }
            var noise = axis == Axis.XAxis
                ? Properties.Settings.Default.OAPAXCalibrationNoise
                : Properties.Settings.Default.OAPAYCalibrationNoise;
            return noise > 0f
                ? (float)System.Math.Max(OapaCalibrationService.NoiseSigmaFactor * noise, OapaCalibrationService.DetectionFloorArcmin)
                : 0f;
        }

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

        /// <summary>
        /// What a hand-entered value costs, applied from every setter that can receive one: a
        /// pending Apply confirmation is cancelled, because it no longer describes anything
        /// real. That prompt names the values it is about to overwrite, so an edit made while
        /// it is up leaves the user one press away from confirming a sentence about numbers
        /// that are no longer there. The gate exists to protect deliberate values, and typing
        /// one in while it is asking is the most deliberate act available - it must not be the
        /// act that switches the gate off.
        /// </summary>
        private void OnManualEdit(OapaParameterSource source) {
            if (source != OapaParameterSource.Manual) { return; }
            ApplyConfirmationPending = false;
        }

        /// <summary>
        /// Whether a value entered in the panel can be taken now. Not while an axis is moving:
        /// Go Home and the calibration work from the values in force when they start, and a
        /// value changed underneath them is executed with the new one. A refused edit is
        /// announced back so the field shows the value still in force.
        /// </summary>
        private bool AcceptsEditNow(string property) {
            if (IsNotMoving) { return true; }
            RaisePropertyChanged(property);
            return false;
        }

        public override float XGearRatio {
            get => Properties.Settings.Default.OAPAXGearRatio;
            set {
                if (!AcceptsEditNow(nameof(XGearRatio))) { return; }
                SetXGearRatio(value, MarkEdit(value, XGearRatio, XGearRatioSource));
            }
        }

        private void SetXGearRatio(float value, OapaParameterSource source) {
            OnManualEdit(source);
            value = System.Math.Clamp(value, MinimumFactor, MaximumFactor);
            Properties.Settings.Default.OAPAXGearRatio = value;
            Properties.Settings.Default.OAPAXGearRatioSource = source.ToString();
            if (upa != null) { upa.XGearRatio = value; }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(XGearRatio));
            RaisePropertyChanged(nameof(XGearRatioSource));
            RaisePropertyChanged(nameof(XGearRatioSourceLabel));
            RaisePropertyChanged(nameof(PositionX));
            RefreshHomeDisplay();
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
                if (!AcceptsEditNow(nameof(YGearRatio))) { return; }
                SetYGearRatio(value, MarkEdit(value, YGearRatio, YGearRatioSource));
            }
        }

        private void SetYGearRatio(float value, OapaParameterSource source) {
            OnManualEdit(source);
            value = System.Math.Clamp(value, MinimumFactor, MaximumFactor);
            Properties.Settings.Default.OAPAYGearRatio = value;
            Properties.Settings.Default.OAPAYGearRatioSource = source.ToString();
            if (upa != null) { upa.YGearRatio = value; }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(YGearRatio));
            RaisePropertyChanged(nameof(YGearRatioSource));
            RaisePropertyChanged(nameof(YGearRatioSourceLabel));
            RaisePropertyChanged(nameof(PositionY));
            RefreshHomeDisplay();
        }

        public override int YSpeed {
            get => Properties.Settings.Default.OAPAYSpeed;
            set {
                Properties.Settings.Default.OAPAYSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        // The Reverse flags carry provenance like the values below: a flag set by hand is a
        // deliberate decision, and Apply asks before flipping it.

        public override bool ReverseAzimuth {
            get => Properties.Settings.Default.OAPAReverseAzimuth;
            set {
                if (!AcceptsEditNow(nameof(ReverseAzimuth))) { return; }
                SetReverseAzimuth(value, value != ReverseAzimuth ? OapaParameterSource.Manual : ReverseAzimuthSource);
            }
        }

        private void SetReverseAzimuth(bool value, OapaParameterSource source) {
            OnManualEdit(source);
            Properties.Settings.Default.OAPAReverseAzimuth = value;
            Properties.Settings.Default.OAPAReverseAzimuthSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(ReverseAzimuth));
            RaisePropertyChanged(nameof(ReverseAzimuthSource));
        }

        public override bool ReverseAltitude {
            get => Properties.Settings.Default.OAPAReverseAltitude;
            set {
                if (!AcceptsEditNow(nameof(ReverseAltitude))) { return; }
                SetReverseAltitude(value, value != ReverseAltitude ? OapaParameterSource.Manual : ReverseAltitudeSource);
            }
        }

        private void SetReverseAltitude(bool value, OapaParameterSource source) {
            OnManualEdit(source);
            Properties.Settings.Default.OAPAReverseAltitude = value;
            Properties.Settings.Default.OAPAReverseAltitudeSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(ReverseAltitude));
            RaisePropertyChanged(nameof(ReverseAltitudeSource));
        }

        public override float XBacklashCompensation {
            get => Properties.Settings.Default.OAPAXBacklashCompensation;
            set {
                if (!AcceptsEditNow(nameof(XBacklashCompensation))) { return; }
                SetXBacklash(value, MarkEdit(value, XBacklashCompensation, XBacklashSource));
            }
        }

        private void SetXBacklash(float value, OapaParameterSource source) {
            OnManualEdit(source);
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAXBacklashCompensation = value;
            Properties.Settings.Default.OAPAXBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(XBacklashCompensation));
            RaisePropertyChanged(nameof(XBacklashSource));
            RaisePropertyChanged(nameof(XBacklashSourceLabel));
        }

        // OAPA-specific: the altitude axis of an OAPA platform also has measurable backlash.
        // Deliberately not part of the shared VM contract - other systems do not model it.
        public float YBacklashCompensation {
            get => Properties.Settings.Default.OAPAYBacklashCompensation;
            set {
                if (!AcceptsEditNow(nameof(YBacklashCompensation))) { return; }
                SetYBacklash(value, MarkEdit(value, YBacklashCompensation, YBacklashSource));
            }
        }

        private void SetYBacklash(float value, OapaParameterSource source) {
            OnManualEdit(source);
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAYBacklashCompensation = value;
            Properties.Settings.Default.OAPAYBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(YBacklashCompensation));
            RaisePropertyChanged(nameof(YBacklashSource));
            RaisePropertyChanged(nameof(YBacklashSourceLabel));
        }

        // ----- Per-direction backlash -----
        // Entering one direction and entering the other are two different physical
        // quantities on an axis loaded by gravity. The pair above holds the positive
        // direction; these hold the negative one, and a stored value below zero means
        // "never set" so an axis configured before this existed stays symmetric instead of
        // silently acquiring a zero compensation one way.

        public float XBacklashCompensationNegative {
            get {
                var stored = Properties.Settings.Default.OAPAXBacklashCompensationNegative;
                return stored < 0f ? XBacklashCompensation : stored;
            }
            set {
                if (!AcceptsEditNow(nameof(XBacklashCompensationNegative))) { return; }
                SetXBacklashNegative(value, MarkEdit(value, XBacklashCompensationNegative, XBacklashSource));
            }
        }

        private void SetXBacklashNegative(float value, OapaParameterSource source) {
            OnManualEdit(source);
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAXBacklashCompensationNegative = value;
            Properties.Settings.Default.OAPAXBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(XBacklashCompensationNegative));
            RaisePropertyChanged(nameof(XBacklashSourceLabel));
        }

        public float YBacklashCompensationNegative {
            get {
                var stored = Properties.Settings.Default.OAPAYBacklashCompensationNegative;
                return stored < 0f ? YBacklashCompensation : stored;
            }
            set {
                if (!AcceptsEditNow(nameof(YBacklashCompensationNegative))) { return; }
                SetYBacklashNegative(value, MarkEdit(value, YBacklashCompensationNegative, YBacklashSource));
            }
        }

        private void SetYBacklashNegative(float value, OapaParameterSource source) {
            OnManualEdit(source);
            value = System.Math.Clamp(value, 0f, MaximumBacklashArcmin);
            Properties.Settings.Default.OAPAYBacklashCompensationNegative = value;
            Properties.Settings.Default.OAPAYBacklashSource = source.ToString();
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(YBacklashCompensationNegative));
            RaisePropertyChanged(nameof(YBacklashSourceLabel));
        }

        // ----- Parameter provenance -----
        // A hand-entered value is a deliberate user decision: it is tracked as Manual and
        // Apply will not replace it without an explicit confirmation. Values written by
        // ApplyCalibration are tracked as Calibrated.

        /// <summary>A public write is a manual edit only when it actually changes the value.</summary>
        private static OapaParameterSource MarkEdit(float newValue, float currentValue, OapaParameterSource currentSource) {
            return System.Math.Abs(newValue - currentValue) > 1e-6f ? OapaParameterSource.Manual : currentSource;
        }

        private static OapaParameterSource ParseSource(string stored) =>
            Enum.TryParse<OapaParameterSource>(stored, out var source) ? source : OapaParameterSource.Default;

        public OapaParameterSource XGearRatioSource => ParseSource(Properties.Settings.Default.OAPAXGearRatioSource);
        public OapaParameterSource YGearRatioSource => ParseSource(Properties.Settings.Default.OAPAYGearRatioSource);
        public OapaParameterSource XBacklashSource => ParseSource(Properties.Settings.Default.OAPAXBacklashSource);
        public OapaParameterSource YBacklashSource => ParseSource(Properties.Settings.Default.OAPAYBacklashSource);
        public OapaParameterSource ReverseAzimuthSource => ParseSource(Properties.Settings.Default.OAPAReverseAzimuthSource);
        public OapaParameterSource ReverseAltitudeSource => ParseSource(Properties.Settings.Default.OAPAReverseAltitudeSource);

        // Small provenance hints next to the fields; empty for factory defaults.
        public string XGearRatioSourceLabel => SourceLabel(XGearRatioSource);
        public string YGearRatioSourceLabel => SourceLabel(YGearRatioSource);
        public string XBacklashSourceLabel => SourceLabel(XBacklashSource);
        public string YBacklashSourceLabel => SourceLabel(YBacklashSource);
        private static string SourceLabel(OapaParameterSource source) =>
            source == OapaParameterSource.Default ? string.Empty : source.ToString().ToLowerInvariant();

        /// <summary>Factor bounds: a value below 1 is meaningless, above this it is a typo.</summary>
        private const float MinimumFactor = 1f;
        private const float MaximumFactor = 100000f;
        /// <summary>Backlash beyond 1.5 degrees is physically absurd and would command huge compensation moves.</summary>
        private const float MaximumBacklashArcmin = 90f;

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
        //
        // Home marks a physical controller position, so it is stored in controller-native
        // units: the displayed logical position is the controller position divided by the
        // gear ratio, and MoveAbsolute multiplies by it again, so a logical value saved
        // under one ratio drives somewhere else once the ratio changes (manual edit or
        // ApplyCalibration). HomeX/HomeY are display-only projections under the current
        // ratio, refreshed by the ratio setters.

        private float homeXController;
        private float homeYController;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GoHomeCommand))]
        private bool hasHome;

        [ObservableProperty]
        private float homeX;

        [ObservableProperty]
        private float homeY;

        public bool CanSetHome() => Connected && IsNotMoving;
        public bool CanGoHome() => Connected && IsNotMoving && HasHome;

        /// <summary>Which axes made it, for a move to home that did not complete.</summary>
        internal static string Arrived(bool azimuthArrived)
            => azimuthArrived
                ? "azimuth reached home, altitude did not"
                : "neither axis reached home";

        private void RefreshHomeDisplay() {
            if (!HasHome) { return; }
            HomeX = homeXController / XGearRatio;
            HomeY = homeYController / YGearRatio;
        }

        [RelayCommand(CanExecute = nameof(CanSetHome))]
        public async Task SetHome(CancellationToken token) {
            // Read from the controller at the moment of the press: the panel's position comes
            // from a background poll and can still show where the axis was before a move that
            // has just finished.
            await upa.RefreshStatus(token).ConfigureAwait(false);
            var x = upa.XPosition1;
            var y = upa.YPosition1;
            await RunOnUi(() => {
                homeXController = x * XGearRatio;
                homeYController = y * YGearRatio;
                HomeX = x;
                HomeY = y;
                HasHome = true;
            });
            Logger.Info($"OAPA home position set to X={HomeX:F2}, Y={HomeY:F2} (controller {homeXController:F0}/{homeYController:F0}, valid for this connection session)");
            Notification.ShowInformation($"Home position saved for this session (X={HomeX:F2}, Y={HomeY:F2})");
        }

        [RelayCommand(CanExecute = nameof(CanGoHome))]
        public async Task GoHome(CancellationToken token) {
            var azimuthArrived = false;
            try {
                await RunOnUi(() => IsNotMoving = false);
                var targetX = homeXController / XGearRatio;
                var targetY = homeYController / YGearRatio;
                Logger.Info($"OAPA moving to home position X={targetX:F2}, Y={targetY:F2} (controller {homeXController:F0}/{homeYController:F0})");
                await upa.MoveAbsolute(Axis.XAxis, XSpeed, targetX, token).ConfigureAwait(false);
                azimuthArrived = true;
                await upa.MoveAbsolute(Axis.YAxis, YSpeed, targetY, token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // The axes are driven one after the other, so a stop between them leaves the
                // platform at home in azimuth and wherever it stood in altitude. Saying nothing
                // leaves somebody looking at a rig that is half where they asked for.
                Logger.Info($"OAPA move to home cancelled; {Arrived(azimuthArrived)}");
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Failed to move to home position: {ex.Message} ({Arrived(azimuthArrived)})");
            } finally {
                await RunOnUi(() => IsNotMoving = true);
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

        // The Reverse flags the pass ended up agreeing with, held here rather than written
        // when the flip is discovered.
        //
        // They used to be persisted the moment an axis came back flipped, which is halfway
        // through a run: cancel during the second axis, read "Cancelled", and a saved setting
        // had changed anyway. Every other measured value waits for Apply - which even asks a
        // second time before overwriting something entered by hand - so this was the one
        // quantity the panel changed behind the user.
        //
        // And the flag is not separable from the factor. A flipped axis is measured by a
        // second pass run with the direction reversed, so the ratio in the outcome is the
        // ratio *under that flag*. Applying one without the other leaves the axis corrected by
        // a number measured in the opposite direction, which is the state a run abandoned
        // before Apply used to leave behind.
        //
        // Nothing during the run depends on them: the service takes the direction as a
        // parameter and flips it internally for its own retry, and the motion layer never
        // reads these settings.
        [ObservableProperty]
        private bool discoveredReverseAzimuth;

        [ObservableProperty]
        private bool discoveredReverseAltitude;

        // The discovered backlash is per direction: these hold the positive one, the pair
        // below the negative one. They are equal on a symmetric axis.
        [ObservableProperty]
        private float discoveredXBacklash;

        [ObservableProperty]
        private float discoveredYBacklash;

        [ObservableProperty]
        private float discoveredXBacklashNegative;

        [ObservableProperty]
        private float discoveredYBacklashNegative;

        // The solve noise each pass measured. It sets how finely that axis can be asked to
        // reverse, so it outlives the session that measured it and is persisted by Apply.
        [ObservableProperty]
        private float discoveredXNoise;

        [ObservableProperty]
        private float discoveredYNoise;

        [ObservableProperty]
        private string calibrationConsistencyMessage = string.Empty;

        // Backlash that costs a different amount in each direction. Reported so the extra
        // convergence cycles are expected rather than mysterious; it does not gate Apply,
        // because the mean compensation is imperfect, not invalid - and the calibration
        // factor, which is the more valuable half of the result, is unaffected by it.
        [ObservableProperty]
        private bool calibrationDirectionalBacklash;

        // Armed by the first Apply when manual values would be replaced; the second Apply
        // confirms. Disarmed by Discard and by starting a new calibration.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ApplyButtonText))]
        private bool applyConfirmationPending;

        /// <summary>
        /// The button states the request itself while a confirmation is armed: leaving it
        /// in the status line alone reads as "nothing happened" and costs a calibration.
        /// </summary>
        public string ApplyButtonText => ApplyConfirmationPending ? "Apply again to confirm" : "Apply";

        /// <summary>
        /// Apply writes the factors straight through to the controller, so it waits for the axes
        /// to stop: a factor changed between the two moves of Go Home, or under a calibration
        /// pass, turns a target computed with one value into a move executed with another.
        /// </summary>
        public bool CanApplyCalibration() => HasCalibrationResult && IsNotMoving;

        public bool CanCalibrate() => Connected && IsNotMoving && !CalibrationRunning && CameraIsFree();

        /// <summary>
        /// Why the Calibrate button is disabled, or empty when it is not.
        ///
        /// A disabled control with no explanation costs a user their night. On 18/08 an
        /// alignment halted, the halt told the user to re-run the Self-Calibration, and the
        /// button was grey: a halt *pauses* the alignment rather than ending it, and a paused
        /// alignment still holds the camera, so the remedy we had just recommended was
        /// unavailable and nothing said so. They rebooted the machine.
        ///
        /// Kept as a pure function of the four conditions so it cannot drift away from
        /// <see cref="CanCalibrate"/>: whatever disables the button is exactly what this
        /// reports.
        /// </summary>
        internal static string CalibrationBlockedBy(bool connected, bool moving, bool calibrating, bool cameraBusy) {
            if (!connected) { return "Connect the controller first."; }
            if (calibrating) { return "A calibration is already running."; }
            if (moving) { return "An axis is still moving."; }
            if (cameraBusy) {
                // The diagnosis alone leaves the user where they were: what unblocks them is
                // knowing the alignment is still running even though it stopped correcting.
                return "The camera is in use by the polar alignment. A halted alignment is only "
                     + "paused, and still holds it - Stop the alignment, then calibrate.";
            }
            return string.Empty;
        }

        public string CalibrateUnavailableReason =>
            CalibrationBlockedBy(Connected, !IsNotMoving, CalibrationRunning, !CameraIsFree());

        // The capture block owner is identified by reference; a dedicated token keeps the
        // camera-consumer plumbing off the public VM surface. A null mediator (headless
        // hosts) means "always free" with no-op acquisition.
        private bool CameraIsFree() => cameraMediator == null || cameraMediator.IsFreeToCapture(cameraBlockToken);

        private sealed class CameraBlockToken : ICameraConsumer {
            private readonly Action onCameraUpdate;

            public CameraBlockToken(Action onCameraUpdate) {
                this.onCameraUpdate = onCameraUpdate;
            }

            public void UpdateDeviceInfo(CameraInfo deviceInfo) => onCameraUpdate();
            public void Dispose() { }
        }

        private sealed class SpeedAwareMotion : IOapaCalibrationMotion {
            private readonly UniversalPolarAlignmentOAPAVM vm;
            public SpeedAwareMotion(UniversalPolarAlignmentOAPAVM vm) { this.vm = vm; }
            public Task MoveRelative(Axis axis, float arcmin, CancellationToken token) {
                var speed = axis == Axis.XAxis ? vm.XSpeed : vm.YSpeed;
                return vm.upa.MoveRelative(axis, speed, arcmin, token);
            }

            /// <summary>
            /// The controller keeps a machine position and reports it on request, so the
            /// status is refreshed rather than read from whatever the last move left behind.
            /// </summary>
            public async Task<float?> ReadPosition(Axis axis, CancellationToken token) {
                await vm.upa.RefreshStatus(token).ConfigureAwait(false);
                return axis == Axis.XAxis ? vm.upa.XPosition1 : vm.upa.YPosition1;
            }

            public Task MoveAbsolute(Axis axis, float position, CancellationToken token) {
                var speed = axis == Axis.XAxis ? vm.XSpeed : vm.YSpeed;
                return vm.upa.MoveAbsolute(axis, speed, position, token);
            }
        }

        [RelayCommand(CanExecute = nameof(CanCalibrate))]
        public Task CalibrateGearRatios(CancellationToken token) {
            // Claimed here, synchronously, before anything is scheduled.
            //
            // The body below runs on a worker, so a flag raised inside it is raised some time
            // after the click. Until then CanCalibrate still answers yes and the button is
            // still live, and a second press starts a second pass over the same two axes: two
            // sequences interleaving moves on the same hardware, each measuring the other's
            // displacements. The camera check is not a guard against this - it is asked with
            // this panel's own token, which its own block does not make busy, and it answers
            // yes outright when there is no camera mediator at all.
            //
            // Commands are invoked on the UI thread, so a plain check and set is enough; what
            // it must not be is asynchronous.
            if (CalibrationRunning) {
                Logger.Info("OAPA self-calibration already running; ignoring the repeated request");
                return Task.CompletedTask;
            }
            CalibrationRunning = true;
            IsNotMoving = false;
            // Withdrawn here for the same reason, and it is the one with teeth. Apply is gated
            // on a result being available, and the previous run's result is still available
            // until this one clears it. Clearing it on the worker leaves a window where Apply
            // is live while a pass is already measuring - and Apply writes the gear ratio
            // straight through to the controller, so the sequence would carry on dividing its
            // displacements by a factor that changed underneath it.
            var hadResult = HasCalibrationResult;
            HasCalibrationResult = false;

            return Task.Run(async () => {
                // CanExecute is evaluated at UI time; re-check here so a sequence or another
                // TPPA run that acquired the camera in the meantime cannot be interrupted.
                if (!CameraIsFree()) {
                    Logger.Warning("OAPA self-calibration refused: another consumer owns the camera");
                    Notification.ShowWarning("Cannot calibrate: the camera is in use by a sequence or another imaging process.");
                    await RunOnUi(() => {
                        CalibrationStatus = "Camera busy - calibration not started";
                        // Nothing was measured, so the previous result is handed back rather
                        // than lost: the discovered values were never touched.
                        HasCalibrationResult = hadResult;
                        CalibrationRunning = false;
                        IsNotMoving = true;
                    });
                    return;
                }
                var captureBlocked = false;
                try {
                    // Inside the try: checking the camera and blocking it are two calls, and the
                    // mediator throws when another consumer took the camera in between. That lost
                    // race has to end the pass like any other failure.
                    cameraMediator?.RegisterCaptureBlock(cameraBlockToken);
                    captureBlocked = true;
                    await RunOnUi(() => {
                        // CalibrationRunning and IsNotMoving are already claimed above.
                        HasCalibrationResult = false;
                        CalibrationDirectionalBacklash = false;
                        ApplyConfirmationPending = false;
                        CalibrationStatus = "Starting calibration...";
                        CalibrationConsistencyMessage = string.Empty;
                    });

                    Logger.Info($"OAPA self-calibration started (settle {AutomatedAdjustmentSettleTime}s between move and solve)");
                    // Same settle the correction loop uses: a high-friction axis is still
                    // relaxing when the controller reports idle, and solving into that
                    // relaxation is what makes the two backlash transitions disagree.
                    var service = new OapaCalibrationService(new SpeedAwareMotion(this), calibrationSolver,
                        settleTime: TimeSpan.FromSeconds(AutomatedAdjustmentSettleTime));
                    Action<string> reportStatus = s => _ = RunOnUi(() => CalibrationStatus = s);

                    var x = await service.CalibrateAxisWithAutoReverse(
                        Axis.XAxis, XGearRatio, ReverseAzimuth, "X (Azimuth)", reportStatus, token);
                    var reverseAzimuthFound = x.Flipped ? !ReverseAzimuth : ReverseAzimuth;
                    var y = await service.CalibrateAxisWithAutoReverse(
                        Axis.YAxis, YGearRatio, ReverseAltitude, "Y (Altitude)", reportStatus, token);
                    var reverseAltitudeFound = y.Flipped ? !ReverseAltitude : ReverseAltitude;

                    string consistencyMsg;
                    if (x.Consistent && y.Consistent) {
                        var notes = new List<string>();
                        if (x.Flipped) { notes.Add("Reverse Az to be corrected on Apply"); }
                        if (y.Flipped) { notes.Add("Reverse Alt to be corrected on Apply"); }
                        consistencyMsg = notes.Count == 0
                            ? "Direction consistency: OK"
                            : "Direction consistency: OK (" + string.Join(", ", notes) + ")";
                    } else {
                        consistencyMsg = $"Direction consistency: WARNING (X={(x.Consistent ? "ok" : "fail")}, Y={(y.Consistent ? "ok" : "fail")}). Auto-flip did not resolve it; check wiring.";
                    }
                    if (x.Asymmetric || y.Asymmetric) {
                        var details = new List<string>();
                        if (x.Asymmetric) { details.Add($"X forward {x.ForwardRatio:F1} / reverse {x.ReverseRatio:F1}"); }
                        if (y.Asymmetric) { details.Add($"Y forward {y.ForwardRatio:F1} / reverse {y.ReverseRatio:F1}"); }
                        consistencyMsg += $" \u26a0 The axis responds differently per direction ({string.Join("; ", details)}). The applied factor is the mean; convergence may take a few extra cycles.";
                    }
                    if (x.ResponseSuspect || y.ResponseSuspect) {
                        var details = new List<string>();
                        if (x.ResponseSuspect) { details.Add($"X forward {x.ForwardRatio:F1} / reverse {x.ReverseRatio:F1}"); }
                        if (y.ResponseSuspect) { details.Add($"Y forward {y.ForwardRatio:F1} / reverse {y.ReverseRatio:F1}"); }
                        consistencyMsg += $" ⛔ The two directions disagree by more than a factor of two ({string.Join("; ", details)}): the weaker one is losing motion (motor stall, slip, binding). The applied factor was taken from the stronger direction alone. Before re-running, check the run current and speed of this axis - a motor without torque margin loses steps against gravity.";
                    }
                    if (x.BacklashSuspect || y.BacklashSuspect) {
                        var axes = new List<string>();
                        if (x.BacklashSuspect) { axes.Add("X"); }
                        if (y.BacklashSuspect) { axes.Add("Y"); }
                        consistencyMsg += $" ⛔ The backlash on {string.Join(" and ", axes)} could not be measured (a reversal came back longer than the response allows), so it is reported as zero rather than as a number that would be applied. Re-run the calibration; if it repeats, measure the play by hand and enter it.";
                    }
                    var directional = x.DirectionalBacklash || y.DirectionalBacklash;
                    if (directional) {
                        var details = new List<string>();
                        if (x.DirectionalBacklash) { details.Add($"X {x.BacklashEnteringPositiveArcmin:F1}' vs {x.BacklashEnteringNegativeArcmin:F1}'"); }
                        if (y.DirectionalBacklash) { details.Add($"Y {y.BacklashEnteringPositiveArcmin:F1}' vs {y.BacklashEnteringNegativeArcmin:F1}'"); }
                        consistencyMsg += $" \u26a0 The backlash costs a different amount in each direction ({string.Join("; ", details)}), which is normal on an axis loaded by gravity. Both figures are recorded, and an unconfirmed split is applied as its mean until a second calibration agrees - but if the two figures also change between calibrations, the mechanics are slipping: check grub screws, belt tension and friction.";
                    }

                    await RunOnUi(() => {
                        DiscoveredXRatio = x.Ratio;
                        DiscoveredYRatio = y.Ratio;
                        DiscoveredReverseAzimuth = reverseAzimuthFound;
                        DiscoveredReverseAltitude = reverseAltitudeFound;
                        DiscoveredXBacklash = x.BacklashEnteringPositiveArcmin;
                        DiscoveredYBacklash = y.BacklashEnteringPositiveArcmin;
                        DiscoveredXBacklashNegative = x.BacklashEnteringNegativeArcmin;
                        DiscoveredYBacklashNegative = y.BacklashEnteringNegativeArcmin;
                        DiscoveredXNoise = x.NoiseSigmaArcmin;
                        DiscoveredYNoise = y.NoiseSigmaArcmin;
                        CalibrationDirectionalBacklash = directional;
                        CalibrationConsistencyMessage = consistencyMsg;
                        CalibrationStatus = $"Done. X={x.Ratio:F2}, Y={y.Ratio:F2}, backlash X={Pair(x)}, Y={Pair(y)}" +
                            (x.RestoredToBaseline && y.RestoredToBaseline ? string.Empty : " ⚠ not returned to start");
                        HasCalibrationResult = true;
                    });

                    Logger.Info($"OAPA calibration result: X={x.Ratio:F2}, Y={y.Ratio:F2}, backlash X={Pair(x)}, Y={Pair(y)}, consistency: X={x.Consistent}, Y={y.Consistent}, " +
                        $"restored: X={x.RestoredToBaseline} ({x.ClosingResidualArcmin:F2}'), Y={y.RestoredToBaseline} ({y.ClosingResidualArcmin:F2}')");
                    // "Measured" and "physically back at the start" are different claims: a
                    // calibration whose closing failed must not be announced as plain success,
                    // or the platform silently keeps the calibration's last displacement.
                    if (x.RestoredToBaseline && y.RestoredToBaseline) {
                        Notification.ShowInformation(
                            $"Calibration done. X factor: {x.Ratio:F2}, Y factor: {y.Ratio:F2}, backlash X: {Pair(x)}, Y: {Pair(y)}",
                            TimeSpan.FromSeconds(30));
                    } else {
                        var offAxes = new List<string>();
                        if (!x.RestoredToBaseline) { offAxes.Add($"Azimuth ({(float.IsNaN(x.ClosingResidualArcmin) ? "residual unknown" : $"{x.ClosingResidualArcmin:F1}' off")})"); }
                        if (!y.RestoredToBaseline) { offAxes.Add($"Altitude ({(float.IsNaN(y.ClosingResidualArcmin) ? "residual unknown" : $"{y.ClosingResidualArcmin:F1}' off")})"); }
                        Notification.ShowWarning(
                            $"Calibration measured (X: {x.Ratio:F2}, Y: {y.Ratio:F2}), but the platform did not verifiably return to its starting position: {string.Join(", ", offAxes)}. " +
                            "The measured factors are valid; re-check your polar alignment before imaging.");
                    }
                } catch (OperationCanceledException) {
                    Logger.Info("OAPA self-calibration cancelled");
                    await RunOnUi(() => CalibrationStatus = "Cancelled");
                } catch (Exception ex) {
                    Logger.Error(ex);
                    Notification.ShowError($"Calibration failed: {ex.Message}");
                    await RunOnUi(() => CalibrationStatus = $"Failed: {ex.Message}");
                } finally {
                    // Only a block this pass took is released; one it lost to another consumer
                    // belongs to that consumer.
                    if (captureBlocked) {
                        cameraMediator?.ReleaseCaptureBlock(cameraBlockToken);
                    }
                    await RunOnUi(() => {
                        CalibrationRunning = false;
                        IsNotMoving = true;
                    });
                }
            });
        }

        /// <summary>
        /// Renders a measured backlash pair the way it will be applied. Every user-facing
        /// string goes through here: printing one of the two directions and calling it "the
        /// measured backlash" is how a rig ran a whole session with 54.34'/45.02' configured
        /// while the panel, the notification and the log all said 54.34'.
        /// </summary>
        private static string Pair(AxisCalibrationOutcome a)
            => a.BacklashEnteringPositiveArcmin == a.BacklashEnteringNegativeArcmin
                ? $"{a.BacklashEnteringPositiveArcmin:F2}'"
                : $"+{a.BacklashEnteringPositiveArcmin:F2}'/-{a.BacklashEnteringNegativeArcmin:F2}'";

        private static string Pair(float positive, float negative)
            => positive == negative ? $"{positive:F2}'" : $"+{positive:F2}'/-{negative:F2}'";

        /// <summary>
        /// What an Apply that died partway has to add to its own failure message.
        ///
        /// These settings are persisted one at a time, so "Failed to apply calibration" is
        /// only the whole truth when nothing was written. Told that alone, a user reasonably
        /// concludes their configuration is untouched and carries on with a platform whose
        /// direction has changed and whose factor has not - which is the one half-state that
        /// makes an axis correct the wrong way.
        /// </summary>
        internal static string PartialApplyNote(IReadOnlyList<string> committed)
            => committed.Count == 0
                ? string.Empty
                : $" Already applied before the failure: {string.Join(", ", committed)}. Re-run the calibration.";

        [RelayCommand(CanExecute = nameof(CanApplyCalibration))]
        public void ApplyCalibration() {
            // The command is disabled while an axis moves, but the method is public: the guard
            // has to hold on every path that reaches it.
            if (!CanApplyCalibration()) { return; }
            // What has actually been written, so a failure partway can say so instead of
            // reporting a clean "failed" over settings that already changed.
            var committed = new List<string>();
            try {
                // Manual values are deliberate user decisions: name them and require a
                // second Apply instead of overwriting silently.
                var manual = new List<string>();
                if (ReverseAzimuth != DiscoveredReverseAzimuth && ReverseAzimuthSource == OapaParameterSource.Manual) { manual.Add($"Reverse Az {ReverseAzimuth} -> {DiscoveredReverseAzimuth}"); }
                if (ReverseAltitude != DiscoveredReverseAltitude && ReverseAltitudeSource == OapaParameterSource.Manual) { manual.Add($"Reverse Alt {ReverseAltitude} -> {DiscoveredReverseAltitude}"); }
                if (XGearRatioSource == OapaParameterSource.Manual) { manual.Add($"X factor {XGearRatio:F1} -> {DiscoveredXRatio:F1}"); }
                if (YGearRatioSource == OapaParameterSource.Manual) { manual.Add($"Y factor {YGearRatio:F1} -> {DiscoveredYRatio:F1}"); }
                if (XBacklashSource == OapaParameterSource.Manual) { manual.Add($"X backlash {Pair(XBacklashCompensation, XBacklashCompensationNegative)} -> {Pair(DiscoveredXBacklash, DiscoveredXBacklashNegative)}"); }
                if (YBacklashSource == OapaParameterSource.Manual) { manual.Add($"Y backlash {Pair(YBacklashCompensation, YBacklashCompensationNegative)} -> {Pair(DiscoveredYBacklash, DiscoveredYBacklashNegative)}"); }
                if (manual.Count > 0 && !ApplyConfirmationPending) {
                    ApplyConfirmationPending = true;
                    CalibrationStatus = $"These values were set manually and would be replaced: {string.Join("; ", manual)}. Press Apply again to confirm.";
                    Logger.Info($"OAPA calibration apply awaiting confirmation over manual values: {string.Join("; ", manual)}");
                    return;
                }
                ApplyConfirmationPending = false;

                // A direction split is only applied once a second calibration agrees with the
                // first about which way costs more. One pass cannot tell a real asymmetry from
                // a slipped measurement, and the two are not equally cheap to get wrong: the
                // difference between the pair lands as a fixed bias on every reversal, so the
                // axis can no longer be corrected by less than that difference. Field evidence
                // on one rig, two consecutive nights, same axis: 1.45'/1.96' and then
                // 2.19'/0.68' - a stable sum with the larger side flipped, which is the
                // signature of slippage rather than of mechanics.
                // Reset All Settings returns the migration schema to zero while this panel stays
                // open. The pair written below has to be stored under the current schema, or the
                // next start would take it for a legacy pair and erase it.
                OapaSettingsMigration.EnsureCurrent();

                var (xPositive, xNegative) = ConfirmedPair(Axis.XAxis, DiscoveredXBacklash, DiscoveredXBacklashNegative);
                var (yPositive, yNegative) = ConfirmedPair(Axis.YAxis, DiscoveredYBacklash, DiscoveredYBacklashNegative);

                // Before the factors, and it has to be: each factor was measured under the
                // direction the pass settled on, so a factor applied while the axis still
                // carries the other direction is a correction scaled by a number measured
                // going the other way.
                // Order matters if this sequence dies partway, which it can: each setter
                // persists, and persistence can fail. Of the two possible half-states, this is
                // the survivable one. A direction applied without its factor corrects the right
                // way by the wrong amount; a factor applied without its direction corrects the
                // wrong way by the right amount, and an axis correcting the wrong way does not
                // converge slowly - it accelerates away. So the direction goes first, and each
                // value is named as soon as it is written, so a failure names exactly those.
                var azimuthFlips = ReverseAzimuth != DiscoveredReverseAzimuth;
                var altitudeFlips = ReverseAltitude != DiscoveredReverseAltitude;
                if (azimuthFlips) { SetReverseAzimuth(DiscoveredReverseAzimuth, OapaParameterSource.Calibrated); committed.Add("Reverse Az"); }
                if (altitudeFlips) { SetReverseAltitude(DiscoveredReverseAltitude, OapaParameterSource.Calibrated); committed.Add("Reverse Alt"); }

                SetXGearRatio(DiscoveredXRatio, OapaParameterSource.Calibrated);
                committed.Add("X factor");
                SetYGearRatio(DiscoveredYRatio, OapaParameterSource.Calibrated);
                committed.Add("Y factor");
                SetXBacklash(xPositive, OapaParameterSource.Calibrated);
                committed.Add("X backlash");
                SetYBacklash(yPositive, OapaParameterSource.Calibrated);
                committed.Add("Y backlash");
                SetXBacklashNegative(xNegative, OapaParameterSource.Calibrated);
                committed.Add("X negative-direction backlash");
                SetYBacklashNegative(yNegative, OapaParameterSource.Calibrated);
                committed.Add("Y negative-direction backlash");
                // The solve noise the pass measured: it sets how finely this axis can be asked
                // to reverse, so it outlives the session that measured it.
                Properties.Settings.Default.OAPAXCalibrationNoise = DiscoveredXNoise;
                Properties.Settings.Default.OAPAYCalibrationNoise = DiscoveredYNoise;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                // Applying the calibration includes picking the backlash strategy the
                // measurements call for; the change is stated explicitly, never silent.
                XBacklashMode = BacklashModePlanner.Recommend(DiscoveredXBacklash, DiscoveredXBacklashNegative, DiscoveredXNoise);
                YBacklashMode = BacklashModePlanner.Recommend(DiscoveredYBacklash, DiscoveredYBacklashNegative, DiscoveredYNoise);
                committed.Add("backlash modes");
                HasCalibrationResult = false;
                // Both messages report the pair that was *applied*, which is not always the pair
                // that was measured: an unconfirmed direction split is applied as its mean. A log
                // line stating the measured split next to a line saying the mean was applied reads
                // as a contradiction, and the panel's reader has no way to tell which one the axis
                // is now using.
                // A flipped direction is named where it is applied. It is the one change here
                // that alters which way the axis moves, so leaving it to be inferred from a
                // checkbox that quietly changed is the same silence this used to have.
                var flips = new List<string>();
                if (azimuthFlips) { flips.Add($"Reverse Az -> {DiscoveredReverseAzimuth}"); }
                if (altitudeFlips) { flips.Add($"Reverse Alt -> {DiscoveredReverseAltitude}"); }
                var flipNote = flips.Count == 0 ? string.Empty : $", {string.Join(", ", flips)}";

                CalibrationStatus = $"Applied. Backlash mode set to X: {XBacklashMode}, Y: {YBacklashMode} " +
                    $"(backlash X {Pair(xPositive, xNegative)}, Y {Pair(yPositive, yNegative)}{flipNote})";
                Logger.Info($"OAPA calibration applied: X={DiscoveredXRatio:F2}, Y={DiscoveredYRatio:F2}, backlash X={Pair(xPositive, xNegative)}, Y={Pair(yPositive, yNegative)}, modes X={XBacklashMode}, Y={YBacklashMode}{flipNote}");
                Notification.ShowInformation($"Calibration applied. X factor: {DiscoveredXRatio:F2}, Y factor: {DiscoveredYRatio:F2}. Backlash mode X: {XBacklashMode}, Y: {YBacklashMode}", TimeSpan.FromSeconds(30));
            } catch (Exception ex) {
                Logger.Error(ex);
                // "Failed" on its own is only true if nothing was written. These settings are
                // persisted one at a time, so a failure partway leaves some of them applied,
                // and a user told the apply failed would reasonably assume none of it took.
                var partial = PartialApplyNote(committed);
                Notification.ShowError($"Failed to apply calibration: {ex.Message}.{partial}");
                CalibrationStatus = $"Apply failed: {ex.Message}.{partial}";
            }
        }

        /// <summary>
        /// Returns the backlash pair to apply: the measured split when a previous calibrated
        /// pair agrees about which direction costs more, the mean of the two otherwise.
        ///
        /// Collapsing is the cheap mistake. A symmetric value's magnitude cancels out of a
        /// two-leg plan, so an imperfect mean costs travel time and nothing else; an
        /// unestablished split costs the axis its ability to be corrected finely, permanently,
        /// until someone recalibrates. So the split has to be earned twice.
        /// </summary>
        private (float positive, float negative) ConfirmedPair(Axis axis, float measuredPositive, float measuredNegative) {
            var mean = (measuredPositive + measuredNegative) / 2f;
            var split = measuredPositive - measuredNegative;

            // The comparison is against what the *previous pass measured*, not against what it
            // applied: an unconfirmed split is applied as its mean, so comparing applied values
            // would find a symmetric pair every time and no split could ever be confirmed.
            var previousSplit = axis == Axis.XAxis
                ? Properties.Settings.Default.OAPAXBacklashSplitLast
                : Properties.Settings.Default.OAPAYBacklashSplitLast;
            if (axis == Axis.XAxis) {
                Properties.Settings.Default.OAPAXBacklashSplitLast = split;
            } else {
                Properties.Settings.Default.OAPAYBacklashSplitLast = split;
            }

            if (System.Math.Abs(split) <= float.Epsilon) { return (measuredPositive, measuredNegative); }

            if (System.Math.Abs(previousSplit) <= float.Epsilon) {
                Logger.Info($"OAPA {axis}: measured a direction split ({measuredPositive:F2}'/{measuredNegative:F2}') with nothing to confirm it against; " +
                    $"applying the mean {mean:F2}' to both directions until a second calibration agrees");
                return (mean, mean);
            }

            if (System.Math.Sign(previousSplit) != System.Math.Sign(split)) {
                Logger.Warning($"OAPA {axis}: the direction split flipped between calibrations " +
                    $"(previous difference {previousSplit:+0.00;-0.00}', now {split:+0.00;-0.00}') - " +
                    $"a flipped split is slippage, not mechanics; applying the mean {mean:F2}' to both directions");
                return (mean, mean);
            }

            Logger.Info($"OAPA {axis}: direction split confirmed by two calibrations " +
                $"(difference {previousSplit:+0.00;-0.00}' then {split:+0.00;-0.00}'); applying {measuredPositive:F2}'/{measuredNegative:F2}' per direction");
            return (measuredPositive, measuredNegative);
        }

        [RelayCommand(CanExecute = nameof(HasCalibrationResult))]
        public void DiscardCalibration() {
            DiscoveredXRatio = 0;
            DiscoveredYRatio = 0;
            DiscoveredXBacklash = 0;
            DiscoveredYBacklash = 0;
            DiscoveredXBacklashNegative = 0;
            DiscoveredYBacklashNegative = 0;
            DiscoveredXNoise = 0;
            DiscoveredYNoise = 0;
            // Back to what the axes are actually configured with, not to false: discarding a
            // result must leave nothing behind that a later Apply could pick up, and these two
            // are the only discovered values whose "empty" is a legitimate setting.
            DiscoveredReverseAzimuth = ReverseAzimuth;
            DiscoveredReverseAltitude = ReverseAltitude;
            HasCalibrationResult = false;
            ApplyConfirmationPending = false;
            CalibrationConsistencyMessage = string.Empty;
            CalibrationStatus = "Discarded";
        }
    }
}
