using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// The OAPA panel: the interface between TPPA and the OAPA controller. It connects (USB or
    /// WiFi), moves the axes by hand, holds the settings the controller is given, and forwards
    /// every error TPPA measures. The controller does the rest - calibration, backlash, every
    /// move of the alignment - so TPPA never drives an OAPA axis itself.
    /// </summary>
    public partial class UniversalPolarAlignmentOAPAVM : UniversalPolarAlignmentBaseVM {
        private readonly OapaErrorForwarder errorForwarder;
        private bool warnedOldFirmware;
        private string lastControllerPhase = "";
        private DateTime? lastTppaReadingUtc;
        private CancellationTokenSource calibrationCts;

        /// <summary>The plugin's eyes for a calibration without a polar alignment: capture and plate-solve.</summary>
        protected IOapaCalibrationSolver calibrationSolver;

        /// <summary>TPPA readings this recent mean a polar alignment is measuring, and the controller calibrates on them.</summary>
        private static readonly TimeSpan TppaStreamWindow = TimeSpan.FromSeconds(30);

        public UniversalPolarAlignmentOAPAVM(
            IProfileService profileService,
            IImagingMediator imagingMediator = null,
            ITelescopeMediator telescopeMediator = null,
            IPlateSolverFactory plateSolverFactory = null,
            IMessageBroker messageBroker = null) : base(profileService) {
            // Before any bound property reads a persisted parameter: the panel is often
            // opened long before the controller is connected.
            OapaSettingsMigration.EnsureCurrent();
            errorForwarder = new OapaErrorForwarder(messageBroker, OnAlignmentError);
            if (imagingMediator != null && plateSolverFactory != null) {
                calibrationSolver = new OapaPlateSolveSampler(profileService, imagingMediator, telescopeMediator, plateSolverFactory);
            }

            // Connected and IsNotMoving live on the base VM, whose generated notifications
            // cannot name the commands declared here. The home position is only meaningful for
            // the current controller session (the counters restart at 0 on power-up).
            PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(IsNotMoving)) {
                    RaisePropertyChanged(nameof(XFactorEditable));
                    RaisePropertyChanged(nameof(YFactorEditable));
                }
                if (e.PropertyName == nameof(Connected) || e.PropertyName == nameof(IsNotMoving)) {
                    if (e.PropertyName == nameof(Connected)) {
                        HasHome = false;
                        lastControllerPhase = "";
                        warnedOldFirmware = false;
                    }
                    RefreshDerivedCommands();
                }
            };
        }

        /// <summary>Connected is flipped from a background task in the base VM, so this is marshalled.</summary>
        private void RefreshDerivedCommands() {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) {
                NotifyDerivedCommands();
            } else {
                dispatcher.BeginInvoke(new Action(NotifyDerivedCommands));
            }
        }

        private void NotifyDerivedCommands() {
            SetHomeCommand.NotifyCanExecuteChanged();
            GoHomeCommand.NotifyCanExecuteChanged();
            CalibrateOnControllerCommand.NotifyCanExecuteChanged();
            JogCommand.NotifyCanExecuteChanged();
            StopMotionCommand.NotifyCanExecuteChanged();
        }

        /// <summary>A headless host has no application; the home commands still clear their state.</summary>
        private static async Task RunOnUi(Action action) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null) {
                await dispatcher.BeginInvoke(action);
            } else {
                action();
            }
        }

        protected override string SystemName => "OAPA System";

        protected override IPolarAlignmentSystem CreateSystem() => IsWifiConnection
            ? UniversalPolarAlignmentOAPA.ConnectOverWifi(WifiAddress)
            : new UniversalPolarAlignmentOAPA();

        // ----- Connection -----

        public const string UsbConnection = "USB";
        public const string WifiConnection = "WiFi";

        public string[] ConnectionModes => new[] { UsbConnection, WifiConnection };

        /// <summary>How the next Connect reaches the controller: the USB port scan, or WiFi by address.</summary>
        public string ConnectionMode {
            get => Properties.Settings.Default.OAPAConnection == WifiConnection ? WifiConnection : UsbConnection;
            set {
                var mode = value == WifiConnection ? WifiConnection : UsbConnection;
                if (Properties.Settings.Default.OAPAConnection == mode) { return; }
                Properties.Settings.Default.OAPAConnection = mode;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged(nameof(ConnectionMode));
                RaisePropertyChanged(nameof(IsWifiConnection));
            }
        }

        public bool IsWifiConnection => ConnectionMode == WifiConnection;

        /// <summary>Controller address on the network, "host" or "host:port" (default port 2323).</summary>
        public string WifiAddress {
            get => Properties.Settings.Default.OAPAWifiAddress;
            set {
                var address = value?.Trim() ?? "";
                if (Properties.Settings.Default.OAPAWifiAddress == address) { return; }
                Properties.Settings.Default.OAPAWifiAddress = address;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged(nameof(WifiAddress));
            }
        }

        // ----- The controller runs the alignment -----

        /// <summary>
        /// Always false: the controller moves the axes, never TPPA. TPPA then measures as in
        /// manual mode, publishes every error - which this panel forwards - and still finishes by
        /// itself below its tolerance. The setter keeps the shared setting, which the other
        /// alignment system uses, but it does not hand an OAPA axis to TPPA.
        /// </summary>
        public override bool DoAutomatedAdjustments {
            get => false;
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
        /// Every error TPPA measures is forwarded to the controller (firmware 1.3.0+), which
        /// decides every move. Off, TPPA only measures and nothing moves by itself.
        /// </summary>
        public bool ControllerAligns {
            get => Properties.Settings.Default.OAPAControllerAligns;
            set {
                if (Properties.Settings.Default.OAPAControllerAligns == value) { return; }
                Properties.Settings.Default.OAPAControllerAligns = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged(nameof(ControllerAligns));
            }
        }

        private string controllerStatus = "";

        /// <summary>What the controller reported after the last forwarded reading.</summary>
        public string ControllerStatus {
            get => controllerStatus;
            private set {
                if (controllerStatus == value) { return; }
                controllerStatus = value;
                RaisePropertyChanged(nameof(ControllerStatus));
            }
        }

        /// <summary>One TPPA reading, in arcminutes, on its way to the controller.</summary>
        internal void OnAlignmentError(double azimuthArcmin, double altitudeArcmin) {
            lastTppaReadingUtc = DateTime.UtcNow;
            if (!ControllerAligns || upa == null || !upa.Connected || upa is not IOapaAlignmentController controller) {
                return;
            }
            if (!controller.RunsAlignment) {
                if (!warnedOldFirmware) {
                    warnedOldFirmware = true;
                    Logger.Warning($"OAPA: the controller needs firmware {UniversalPolarAlignmentOAPA.AlignmentFirmwareVersion} or later to align; the readings are not forwarded");
                    ControllerStatus = $"needs firmware {UniversalPolarAlignmentOAPA.AlignmentFirmwareVersion} or later";
                }
                return;
            }
            try {
                var reply = controller.ForwardError(azimuthArcmin, altitudeArcmin);
                var status = controller.AlignmentStatus();
                Logger.Info($"OAPA controller: reading az {azimuthArcmin:F2}' alt {altitudeArcmin:F2}' -> {reply}; {status}");
                ControllerStatus = OapaControllerStatus.Describe(status);
                var phase = OapaControllerStatus.Parse(status).TryGetValue("phase", out var p) ? p : "";
                if (lastControllerPhase == "calibrating" && phase != "calibrating") {
                    TakeControllerCalibration(controller);
                }
                lastControllerPhase = phase;
            } catch (Exception ex) {
                Logger.Error($"OAPA controller: forwarding the reading failed: {ex.Message}");
                ControllerStatus = $"forwarding failed: {ex.Message}";
            }
        }

        /// <summary>
        /// A calibration the controller finished is kept here too: the factors are what the
        /// manual moves of this panel convert with, and what is pushed on the next connection.
        /// </summary>
        private void TakeControllerCalibration(IOapaAlignmentController controller) {
            var line = controller.ControllerCalibrationStatus();
            var factors = OapaControllerStatus.CalibratedFactors(line);
            Logger.Info($"OAPA controller calibration: {line}");
            if (factors == null) {
                return;
            }
            SetXGearRatio(factors.Value.x, OapaParameterSource.Calibrated);
            SetYGearRatio(factors.Value.y, OapaParameterSource.Calibrated);
            // A measured factor replaces one derived from a gear ratio: the axes show it as such.
            SetFactorMode(true, FactorModeSteps);
            SetFactorMode(false, FactorModeSteps);
            Logger.Info($"OAPA controller calibration applied: AZ (X) {factors.Value.x} / ALT (Y) {factors.Value.y} steps per arcmin");
            // The play is kept as the mean of the two directions, as the controller uses it:
            // storing the split pair would push it back on the next connection, and a split the
            // mechanism does not have biases every reversal.
            foreach (var axis in new[] { 'X', 'Y' }) {
                var play = OapaControllerStatus.CalibratedPlay(line, axis);
                if (play == null) { continue; }
                StoreCalibratedPlay(axis == 'X', play.Value.mean, play.Value.mode);
                var name = axis == 'X' ? "AZ (X)" : "ALT (Y)";
                Logger.Info($"OAPA controller calibration applied: {name} play {play.Value.mean:F2}', mode {play.Value.mode}");
            }
        }

        private bool calibrationRunning;

        /// <summary>A calibration fed by the plugin's own frames is under way.</summary>
        public bool CalibrationRunning {
            get => calibrationRunning;
            private set {
                if (calibrationRunning == value) { return; }
                calibrationRunning = value;
                RaisePropertyChanged(nameof(CalibrationRunning));
                RefreshDerivedCommands();
            }
        }

        private bool CanCalibrateOnController() =>
            Connected && !CalibrationRunning && upa is IOapaAlignmentController controller && controller.RunsAlignment;

        /// <summary>
        /// While a polar alignment is measuring, the controller calibrates on TPPA's readings and
        /// aligns after. Otherwise the plugin captures and solves frames itself and feeds the
        /// controller the field's displacements: the controller moves the axes and measures the
        /// factors and the play, and the result is kept here.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanCalibrateOnController))]
        private async Task CalibrateOnController() {
            if (upa is not IOapaAlignmentController controller) { return; }
            if (lastTppaReadingUtc.HasValue && DateTime.UtcNow - lastTppaReadingUtc.Value < TppaStreamWindow) {
                try {
                    var reply = controller.RequestCalibration();
                    Logger.Info($"OAPA controller: calibration on TPPA's readings requested -> {reply}");
                    ControllerStatus = "calibrating on TPPA's readings, then aligning";
                } catch (Exception ex) {
                    Logger.Error($"OAPA controller: requesting a calibration failed: {ex.Message}");
                    ControllerStatus = $"calibration request failed: {ex.Message}";
                }
                return;
            }
            if (calibrationSolver == null) {
                ControllerStatus = "no camera or plate solver available to calibrate";
                return;
            }

            CalibrationRunning = true;
            // The axes are the controller's until it is done: every control that moves them or
            // changes what it works with is off, STOP excepted (it only needs a connection).
            await RunOnUi(() => IsNotMoving = false);
            calibrationCts = new CancellationTokenSource();
            ControllerStatus = "calibrating: capturing the first frame";
            try {
                var line = await Task.Run(() => OapaCalibrationFeed.Run(calibrationSolver, controller,
                    status => ControllerStatus = status, calibrationCts.Token)).ConfigureAwait(false);
                TakeControllerCalibration(controller);
                var result = OapaControllerStatus.Parse(line, "K");
                ControllerStatus = result.TryGetValue("state", out var state) && state == "done"
                    ? "calibration done: " + (result.TryGetValue("reason", out var why) ? why : "")
                    : "calibration ended: " + (result.TryGetValue("reason", out var reason) ? reason : line);
            } catch (OperationCanceledException) {
                StopControllerCalibration(controller);
                ControllerStatus = "calibration stopped";
            } catch (Exception ex) {
                Logger.Error($"OAPA calibration failed: {ex.Message}");
                StopControllerCalibration(controller);
                ControllerStatus = $"calibration failed: {ex.Message}";
            } finally {
                calibrationCts?.Dispose();
                calibrationCts = null;
                CalibrationRunning = false;
                await RunOnUi(() => IsNotMoving = true);
            }
        }

        private static void StopControllerCalibration(IOapaAlignmentController controller) {
            try {
                controller.StopCalibration();
            } catch (Exception ex) {
                Logger.Error($"OAPA calibration: stopping the controller's calibration failed: {ex.Message}");
            }
        }

        // ----- Where the factor comes from: steps per arcminute (calibrated or typed in), or a
        // known gear ratio the plugin turns into steps per arcminute. -----

        public const string FactorModeSteps = "Steps/arcmin";
        public const string FactorModeGear = "Gear ratio";
        private const float ArcminPerRevolution = 360f * 60f;

        public string[] FactorModes => new[] { FactorModeSteps, FactorModeGear };

        /// <summary>
        /// Steps per arcminute of an axis whose reduction is known: motor full steps per
        /// revolution x microsteps x gear ratio (motor turns per axis turn), over the arcminutes
        /// of a turn. 200 x 16 x 100 / 21600 = 14.81.
        /// </summary>
        internal static float FactorFromGear(float gearRatio, int motorStepsPerRev, int microsteps) =>
            motorStepsPerRev * microsteps * gearRatio / ArcminPerRevolution;

        public string XFactorMode {
            get => Properties.Settings.Default.OAPAXFactorMode == FactorModeGear ? FactorModeGear : FactorModeSteps;
            set => SetFactorMode(true, value);
        }

        public string YFactorMode {
            get => Properties.Settings.Default.OAPAYFactorMode == FactorModeGear ? FactorModeGear : FactorModeSteps;
            set => SetFactorMode(false, value);
        }

        public bool XIsGearMode => XFactorMode == FactorModeGear;
        public bool YIsGearMode => YFactorMode == FactorModeGear;

        /// <summary>The factor is typed in only in steps-per-arcminute mode, and not while an axis moves.</summary>
        public bool XFactorEditable => IsNotMoving && !XIsGearMode;
        public bool YFactorEditable => IsNotMoving && !YIsGearMode;

        private void SetFactorMode(bool isX, string value) {
            var mode = value == FactorModeGear ? FactorModeGear : FactorModeSteps;
            if (isX) {
                Properties.Settings.Default.OAPAXFactorMode = mode;
            } else {
                Properties.Settings.Default.OAPAYFactorMode = mode;
            }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            if (mode == FactorModeGear) { ApplyGearFactor(isX); }
            RaisePropertyChanged(isX ? nameof(XFactorMode) : nameof(YFactorMode));
            RaisePropertyChanged(isX ? nameof(XIsGearMode) : nameof(YIsGearMode));
            RaisePropertyChanged(isX ? nameof(XFactorEditable) : nameof(YFactorEditable));
        }

        /// <summary>Motor turns per axis turn (e.g. 100 for a 100:1 worm). 0 until known.</summary>
        public float XMechanicalRatio {
            get => Properties.Settings.Default.OAPAXMechanicalRatio;
            set { Properties.Settings.Default.OAPAXMechanicalRatio = Math.Max(0f, value); GearInputChanged(true, nameof(XMechanicalRatio)); }
        }

        public float YMechanicalRatio {
            get => Properties.Settings.Default.OAPAYMechanicalRatio;
            set { Properties.Settings.Default.OAPAYMechanicalRatio = Math.Max(0f, value); GearInputChanged(false, nameof(YMechanicalRatio)); }
        }

        /// <summary>Full steps per motor revolution: 200 for a 1.8 deg motor, 400 for a 0.9 deg one.</summary>
        public int XMotorStepsPerRev {
            get => Properties.Settings.Default.OAPAXMotorStepsPerRev;
            set { Properties.Settings.Default.OAPAXMotorStepsPerRev = Math.Clamp(value, 1, 100000); GearInputChanged(true, nameof(XMotorStepsPerRev)); }
        }

        public int YMotorStepsPerRev {
            get => Properties.Settings.Default.OAPAYMotorStepsPerRev;
            set { Properties.Settings.Default.OAPAYMotorStepsPerRev = Math.Clamp(value, 1, 100000); GearInputChanged(false, nameof(YMotorStepsPerRev)); }
        }

        // ----- Microstepping -----
        // Trades resolution for speed and torque, and polar alignment has resolution to
        // spare: a platform at 1000 steps per arcminute is two orders of magnitude past
        // what the plate solve can resolve, and pays for it in speed.
        //
        // Steps per arcminute scale exactly with the microstep setting, so changing it
        // invalidates the calibration factor by a known factor. Rescaling it here is not a
        // convenience: leaving a stale factor behind would make every commanded move wrong
        // by that same ratio, and on a short-travel platform the first move would drive an
        // axis into its end stop. The backlash is in physical arcminutes and does not scale.
        // A factor computed from a gear ratio is proportional to the microsteps as well, so
        // the rescale gives it exactly the value the gear ratio would.

        /// <summary>Instance property on purpose: a XAML {Binding} cannot resolve a static one.</summary>
        public int[] MicrostepOptions => SupportedMicrosteps;

        private static readonly int[] SupportedMicrosteps = { 1, 2, 4, 8, 16, 32, 64, 128, 256 };

        public int XMicrosteps {
            get => Properties.Settings.Default.OAPAXMicrosteps;
            set => SetMicrosteps(Axis.XAxis, value);
        }

        public int YMicrosteps {
            get => Properties.Settings.Default.OAPAYMicrosteps;
            set => SetMicrosteps(Axis.YAxis, value);
        }

        private void SetMicrosteps(Axis axis, int value) {
            if (Array.IndexOf(SupportedMicrosteps, value) < 0) { return; }
            var isX = axis == Axis.XAxis;
            var previous = isX ? XMicrosteps : YMicrosteps;
            if (previous == value) { return; }

            if (isX) { Properties.Settings.Default.OAPAXMicrosteps = value; } else { Properties.Settings.Default.OAPAYMicrosteps = value; }

            var scale = (float)value / previous;
            var oldRatio = isX ? XGearRatio : YGearRatio;
            var newRatio = System.Math.Clamp(oldRatio * scale, MinimumFactor, MaximumFactor);
            if (isX) { SetXGearRatio(newRatio, XGearRatioSource); } else { SetYGearRatio(newRatio, YGearRatioSource); }

            CoreUtil.SaveSettings(Properties.Settings.Default);
            Logger.Info($"OAPA microsteps {axis}: {previous} -> {value}; calibration factor rescaled {oldRatio:F2} -> {newRatio:F2} steps/arcmin");
            if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                oapa.SetMicrosteps(axis, value);
            }
            PushControllerParameters();

            RaisePropertyChanged(isX ? nameof(XMicrosteps) : nameof(YMicrosteps));
            RaisePropertyChanged(isX ? nameof(XSpeedPhysical) : nameof(YSpeedPhysical));
        }

        private void GearInputChanged(bool isX, string property) {
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(property);
            if (isX ? XIsGearMode : YIsGearMode) { ApplyGearFactor(isX); }
        }

        private void ApplyGearFactor(bool isX) {
            var ratio = isX ? XMechanicalRatio : YMechanicalRatio;
            if (ratio <= 0f) { return; }  // not known yet: the factor in force stays
            var factor = FactorFromGear(ratio, isX ? XMotorStepsPerRev : YMotorStepsPerRev, isX ? XMicrosteps : YMicrosteps);
            if (isX) {
                SetXGearRatio(factor, OapaParameterSource.Gear);
            } else {
                SetYGearRatio(factor, OapaParameterSource.Gear);
            }
            PushControllerParameters();
        }

        /// <summary>
        /// The controller aligns with the values it was given, not with the ones shown here: a
        /// value changed while connected is sent at once, not at the next connection. Not used
        /// for a calibration result, which the controller already holds.
        /// </summary>
        private void PushControllerParameters() {
            if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                oapa.ApplyControllerParameters();
            }
        }

        // ----- Settings the controller is given -----

        /// <summary>
        /// Manual moves are plain moves: the controller compensates backlash in the moves of its
        /// own alignment, where it knows which side the axis rests on.
        /// </summary>
        protected override float GetBacklashCompensation(Axis axis) => 0f;

        /// <summary>A value typed in the panel while an axis moves (Go Home) is refused, and the field shows the value in force.</summary>
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
                PushControllerParameters();
            }
        }

        private void SetXGearRatio(float value, OapaParameterSource source) {
            value = Math.Clamp(value, MinimumFactor, MaximumFactor);
            Properties.Settings.Default.OAPAXGearRatio = value;
            Properties.Settings.Default.OAPAXGearRatioSource = source.ToString();
            if (upa != null) { upa.XGearRatio = value; }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(XGearRatio));
            RaisePropertyChanged(nameof(XGearRatioSource));
            RaisePropertyChanged(nameof(XGearRatioSourceLabel));
            RaisePropertyChanged(nameof(PositionX));
            RaisePropertyChanged(nameof(XSpeedPhysical));
            RefreshHomeDisplay();
        }

        public override float YGearRatio {
            get => Properties.Settings.Default.OAPAYGearRatio;
            set {
                if (!AcceptsEditNow(nameof(YGearRatio))) { return; }
                SetYGearRatio(value, MarkEdit(value, YGearRatio, YGearRatioSource));
            }
        }

        private void SetYGearRatio(float value, OapaParameterSource source) {
            value = Math.Clamp(value, MinimumFactor, MaximumFactor);
            Properties.Settings.Default.OAPAYGearRatio = value;
            Properties.Settings.Default.OAPAYGearRatioSource = source.ToString();
            if (upa != null) { upa.YGearRatio = value; }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(nameof(YGearRatio));
            RaisePropertyChanged(nameof(YGearRatioSource));
            RaisePropertyChanged(nameof(YGearRatioSourceLabel));
            RaisePropertyChanged(nameof(PositionY));
            RaisePropertyChanged(nameof(YSpeedPhysical));
            RefreshHomeDisplay();
        }

        public override int XSpeed {
            get => Properties.Settings.Default.OAPAXSpeed;
            set {
                Properties.Settings.Default.OAPAXSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(XSpeedPhysical));
            }
        }

        public override int YSpeed {
            get => Properties.Settings.Default.OAPAYSpeed;
            set {
                Properties.Settings.Default.OAPAYSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(YSpeedPhysical));
            }
        }

        // The speed setting is a step rate, and the same number means very different sky
        // speeds on the two axes: on one tester's rig 1000 steps/s is ~74 '/s in azimuth and
        // ~8.6 '/s in altitude. The rate is shown as it actually is, once a calibration
        // factor makes it computable.
        public string XSpeedPhysical => PhysicalSpeed(XSpeed, XGearRatio);

        public string YSpeedPhysical => PhysicalSpeed(YSpeed, YGearRatio);

        private static string PhysicalSpeed(int stepsPerSecond, float stepsPerArcmin) {
            // A factor of 1 is the factory default: the platform has never been calibrated,
            // and inventing a reading from it would be worse than showing none.
            if (stepsPerArcmin <= 1f) { return string.Empty; }
            var arcminPerSecond = stepsPerSecond / stepsPerArcmin;
            return $"~ {arcminPerSecond.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} '/s";
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

        /// <summary>Per-axis backlash handling mode for the controller's moves; unrecognised stored values fall back to Full.</summary>
        public OapaBacklashMode XBacklashMode {
            get => ParseMode(Properties.Settings.Default.OAPAXBacklashMode);
            set {
                Properties.Settings.Default.OAPAXBacklashMode = value.ToString();
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(XBacklashModeName));
                PushControllerParameters();
            }
        }

        public OapaBacklashMode YBacklashMode {
            get => ParseMode(Properties.Settings.Default.OAPAYBacklashMode);
            set {
                Properties.Settings.Default.OAPAYBacklashMode = value.ToString();
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(YBacklashModeName));
                PushControllerParameters();
            }
        }

        private static OapaBacklashMode ParseMode(string stored) =>
            Enum.TryParse<OapaBacklashMode>(stored, out var mode) ? mode : OapaBacklashMode.Full;

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

        public override float XBacklashCompensation {
            get => Properties.Settings.Default.OAPAXBacklashCompensation;
            set => SetBacklash(nameof(XBacklashCompensation), value, v => Properties.Settings.Default.OAPAXBacklashCompensation = v,
                XBacklashCompensation, isX: true);
        }

        public float YBacklashCompensation {
            get => Properties.Settings.Default.OAPAYBacklashCompensation;
            set => SetBacklash(nameof(YBacklashCompensation), value, v => Properties.Settings.Default.OAPAYBacklashCompensation = v,
                YBacklashCompensation, isX: false);
        }

        // Entering the negative direction; a stored value below zero means "never set", so the
        // axis stays symmetric instead of silently acquiring a zero compensation one way.
        public float XBacklashCompensationNegative {
            get {
                var stored = Properties.Settings.Default.OAPAXBacklashCompensationNegative;
                return stored < 0f ? XBacklashCompensation : stored;
            }
            set => SetBacklash(nameof(XBacklashCompensationNegative), value, v => Properties.Settings.Default.OAPAXBacklashCompensationNegative = v,
                XBacklashCompensationNegative, isX: true);
        }

        public float YBacklashCompensationNegative {
            get {
                var stored = Properties.Settings.Default.OAPAYBacklashCompensationNegative;
                return stored < 0f ? YBacklashCompensation : stored;
            }
            set => SetBacklash(nameof(YBacklashCompensationNegative), value, v => Properties.Settings.Default.OAPAYBacklashCompensationNegative = v,
                YBacklashCompensationNegative, isX: false);
        }

        private void StoreCalibratedPlay(bool isX, float mean, OapaBacklashMode mode) {
            var value = Math.Clamp(mean, 0f, MaximumBacklashArcmin);
            var source = OapaParameterSource.Calibrated.ToString();
            if (isX) {
                Properties.Settings.Default.OAPAXBacklashCompensation = value;
                Properties.Settings.Default.OAPAXBacklashCompensationNegative = value;
                Properties.Settings.Default.OAPAXBacklashSource = source;
                Properties.Settings.Default.OAPAXBacklashMode = mode.ToString();
            } else {
                Properties.Settings.Default.OAPAYBacklashCompensation = value;
                Properties.Settings.Default.OAPAYBacklashCompensationNegative = value;
                Properties.Settings.Default.OAPAYBacklashSource = source;
                Properties.Settings.Default.OAPAYBacklashMode = mode.ToString();
            }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(isX ? nameof(XBacklashCompensation) : nameof(YBacklashCompensation));
            RaisePropertyChanged(isX ? nameof(XBacklashCompensationNegative) : nameof(YBacklashCompensationNegative));
            RaisePropertyChanged(isX ? nameof(XBacklashSourceLabel) : nameof(YBacklashSourceLabel));
            RaisePropertyChanged(isX ? nameof(XBacklashMode) : nameof(YBacklashMode));
            RaisePropertyChanged(isX ? nameof(XBacklashModeName) : nameof(YBacklashModeName));
        }

        private void SetBacklash(string property, float value, Action<float> store, float current, bool isX) {
            if (!AcceptsEditNow(property)) { return; }
            var source = MarkEdit(value, current, isX ? XBacklashSource : YBacklashSource);
            store(Math.Clamp(value, 0f, MaximumBacklashArcmin));
            if (isX) {
                Properties.Settings.Default.OAPAXBacklashSource = source.ToString();
            } else {
                Properties.Settings.Default.OAPAYBacklashSource = source.ToString();
            }
            CoreUtil.SaveSettings(Properties.Settings.Default);
            RaisePropertyChanged(property);
            RaisePropertyChanged(isX ? nameof(XBacklashSourceLabel) : nameof(YBacklashSourceLabel));
            PushControllerParameters();
        }

        // ----- Where a value came from: shown next to the fields, and it decides whether the
        // factors are pushed (factory defaults are not: the controller calibrates itself). -----

        private static OapaParameterSource MarkEdit(float newValue, float currentValue, OapaParameterSource currentSource) =>
            Math.Abs(newValue - currentValue) > 1e-6f ? OapaParameterSource.Manual : currentSource;

        private static OapaParameterSource ParseSource(string stored) =>
            Enum.TryParse<OapaParameterSource>(stored, out var source) ? source : OapaParameterSource.Default;

        public OapaParameterSource XGearRatioSource => ParseSource(Properties.Settings.Default.OAPAXGearRatioSource);
        public OapaParameterSource YGearRatioSource => ParseSource(Properties.Settings.Default.OAPAYGearRatioSource);
        public OapaParameterSource XBacklashSource => ParseSource(Properties.Settings.Default.OAPAXBacklashSource);
        public OapaParameterSource YBacklashSource => ParseSource(Properties.Settings.Default.OAPAYBacklashSource);

        public string XGearRatioSourceLabel => SourceLabel(XGearRatioSource);
        public string YGearRatioSourceLabel => SourceLabel(YGearRatioSource);
        public string XBacklashSourceLabel => SourceLabel(XBacklashSource);
        public string YBacklashSourceLabel => SourceLabel(YBacklashSource);
        private static string SourceLabel(OapaParameterSource source) =>
            source == OapaParameterSource.Default ? string.Empty : source.ToString().ToLowerInvariant();

        /// <summary>Factor bounds: below 1 is meaningless, above this it is a typo.</summary>
        private const float MinimumFactor = 1f;
        private const float MaximumFactor = 100000f;
        /// <summary>Backlash beyond 1.5 degrees is physically absurd.</summary>
        private const float MaximumBacklashArcmin = 90f;

        // ----- Driver settings, pushed live when connected -----

        public int XRunCurrent {
            get => Properties.Settings.Default.OAPAXRunCurrent;
            set {
                Properties.Settings.Default.OAPAXRunCurrent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) { oapa.SetXRunCurrent(value); }
            }
        }

        public int YRunCurrent {
            get => Properties.Settings.Default.OAPAYRunCurrent;
            set {
                Properties.Settings.Default.OAPAYRunCurrent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) { oapa.SetYRunCurrent(value); }
            }
        }

        public int XHoldPercent {
            get => Properties.Settings.Default.OAPAXHoldPercent;
            set {
                Properties.Settings.Default.OAPAXHoldPercent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) { oapa.SetXHoldPercent(value); }
            }
        }

        public int YHoldPercent {
            get => Properties.Settings.Default.OAPAYHoldPercent;
            set {
                Properties.Settings.Default.OAPAYHoldPercent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) { oapa.SetYHoldPercent(value); }
            }
        }

        // ----- Arrow pad: up/down move the altitude, left/right the azimuth, by the step -----

        public float[] JogSteps => new[] { 0.1f, 1f, 5f, 10f, 30f };

        private float jogStep = 1f;

        /// <summary>How far one press of an arrow moves the axis, in arcminutes.</summary>
        public float JogStep {
            get => jogStep;
            set {
                if (value <= 0f || jogStep == value) { return; }
                jogStep = value;
                RaisePropertyChanged(nameof(JogStep));
            }
        }

        /// <summary>
        /// The move one arrow makes: "up"/"down" on the altitude, "right"/"left" on the azimuth,
        /// positive for up and right. The Reverse options flip the axis downstream, as for every
        /// hand move.
        /// </summary>
        internal static (Axis axis, float arcmin)? JogMove(string direction, float step) => direction switch {
            "up" => (Axis.YAxis, step),
            "down" => (Axis.YAxis, -step),
            "right" => (Axis.XAxis, step),
            "left" => (Axis.XAxis, -step),
            _ => null
        };

        private bool CanJog() => Connected && IsNotMoving && !CalibrationRunning;

        [RelayCommand(CanExecute = nameof(CanJog))]
        private async Task Jog(string direction, CancellationToken token) {
            var move = JogMove(direction, JogStep);
            if (move == null) { return; }
            if (move.Value.axis == Axis.XAxis) {
                await TryNudgeX(move.Value.arcmin, token);
            } else {
                await TryNudgeY(move.Value.arcmin, token);
            }
        }

        // Available whenever connected - including while a move or the calibration is driving
        // the motors, which is precisely when it is needed.
        public bool CanStopMotion() => Connected;

        [RelayCommand(CanExecute = nameof(CanStopMotion))]
        private void StopMotion() {
            calibrationCts?.Cancel();
            if (upa is not UniversalPolarAlignmentOAPA oapa) { return; }
            if (oapa.TryRequestStop()) { return; }

            // The one button whose failure must never be quiet. Somebody pressing Stop is
            // watching the platform go somewhere they do not want it to go, and a halt that
            // did not reach the controller looks exactly like one that did: nothing happens
            // on screen either way, except that in one case the axis is still moving.
            Notification.ShowError("Stop was not delivered to the controller - the axis may still be moving. "
                + "Check the connection, and cut power to the controller if it does not stop.");
        }

        // ----- Home position (session-scoped) -----
        // The controller's counters restart at 0 on power-up, so home lives in this session
        // only, in controller units: a value saved under one factor would drive elsewhere
        // once the factor changes. HomeX/HomeY are display projections under the current factor.

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
            // Read at the press: the panel's position comes from a background poll.
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
                Logger.Info($"OAPA move to home cancelled; {Arrived(azimuthArrived)}");
            } catch (Exception ex) {
                Logger.Error(ex);
                Notification.ShowError($"Failed to move to home position: {ex.Message} ({Arrived(azimuthArrived)})");
            } finally {
                await RunOnUi(() => IsNotMoving = true);
            }
        }
    }
}
