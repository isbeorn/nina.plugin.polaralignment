using NINA.Core.Utility;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NINA.Plugins.PolarAlignment.OAPA {
    public partial class UniversalPolarAlignmentOAPA : UniversalPolarAlignmentBase, IOapaAlignmentController {
        protected override string SystemName => "OAPA System";
        protected override string NewLineSequence => "\n";
        // ESP32 (CH340) auto-resets when the host opens the port: needs ~1.5s to finish booting
        // and emit its banner before answering. Two probes give the firmware a second chance if
        // the first reply was the boot text instead of the GRBL status frame.
        protected override int ScanReadTimeout => 1500;
        protected override int ScanWriteTimeout => 500;
        protected override bool ClearBufferOnConnect => true;
        protected override int PostOpenDelayMs => 1500;
        protected override int ConnectRetryAttempts => 2;

        // A USB-serial adapter next to a stalling stepper can re-enumerate mid-session: the
        // link is reopened instead of failing the move.
        protected override bool RecoversLink => true;

        // The alignment instruction builds a new system object on every run while the
        // platform keeps its position, so the side of its play each axis rests on is kept.
        protected override bool RemembersEngagementAcrossInstances => true;

        // Try the last successfully matched port first, so a returning user skips the full
        // COM-port scan (~1.5s + ~3s of retries per dead port) and connects in a single probe.
        protected override string PreferredPortName => Properties.Settings.Default.OAPALastPort;

        protected override void OnPortMatched(string portName) {
            if (Properties.Settings.Default.OAPALastPort != portName) {
                Properties.Settings.Default.OAPALastPort = portName;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        // The firmware reports its version in the status frame (V: field) starting with 1.1.0.
        protected override string MinimumFirmwareVersion => "1.1.0";
        protected override string FirmwareReferenceUrl => "https://github.com/michelebergo/oapa-firmware";

        private float xGearRatio = Properties.Settings.Default.OAPAXGearRatio;
        private float yGearRatio = Properties.Settings.Default.OAPAYGearRatio;

        public override float XGearRatio { get => xGearRatio; set => xGearRatio = value; }
        public override float YGearRatio { get => yGearRatio; set => yGearRatio = value; }

        // Ratio-agnostic motion completion overrides (OAPA-only, Avalon unaffected).
        // Tolerance is expressed in physical units (arcmin) and converted to steps via the
        // current gear ratio, so the same precision target works for any user's hardware.
        // ANGULAR_TOLERANCE_ARCMIN sets the "close enough" criterion in sky units.
        private const float ANGULAR_TOLERANCE_ARCMIN = 0.01f;
        // Stuck detection allows for one-step jitter at any ratio.
        protected override float CompletionToleranceSteps(float gearRatio) {
            // Always allow at least 1 step of slack; below 1 step is unreachable on any motor.
            var ratio = Math.Abs(gearRatio);
            if (ratio < 1e-6f) return 1.0f;
            return Math.Max(1.0f, ANGULAR_TOLERANCE_ARCMIN * ratio);
        }
        protected override float StuckDeltaSteps(float gearRatio) {
            // "Not moving" means change of less than 1 step between polls.
            return 1.0f;
        }
        protected override float RoundTarget(float target) {
            // Motor positions are integer steps; comparing against a fractional target
            // would never match, causing false timeouts.
            return (float)Math.Round(target);
        }

        protected override Regex GetStatusRegex() => StatusRegex();

        // The firmware only parses the type-first grammar ("CX600", "HY50"); the axis-first
        // form previously sent here fell into its unknown-command branch and was silently
        // ignored, leaving the drivers on their 600 mA / 50% firmware defaults.
        public UniversalPolarAlignmentOAPA() : base() {
            ApplyStoredDriverConfiguration();
            ApplyControllerParameters();
        }

        // Talks to an already-open link: same post-connect behavior as the scanning constructor
        // (status probe in the base, then the driver-configuration push), no COM scan.
        protected UniversalPolarAlignmentOAPA(ISerialLink link) : base(link) {
            ApplyStoredDriverConfiguration();
            ApplyControllerParameters();
        }

        /// <summary>
        /// Connects over WiFi instead of scanning the COM ports: "host" or "host:port"
        /// (default port 2323, firmware 1.3.0+). From here on the controller behaves exactly
        /// as on USB.
        /// </summary>
        public static UniversalPolarAlignmentOAPA ConnectOverWifi(string address) {
            var link = OapaTcpLink.Connect(address);
            try {
                var system = new UniversalPolarAlignmentOAPA(link);
                Logger.Info($"Found OAPA System over WiFi at {link.Address}");
                return system;
            } catch {
                link.Dispose();
                throw;
            }
        }

        // Driver settings are volatile on the controller (lost on power cycle), so the
        // persisted values are pushed once per connection instead of only on field edits.
        private void ApplyStoredDriverConfiguration() {
            OapaSettingsMigration.EnsureCurrent();
            var settings = Properties.Settings.Default;
            foreach (var command in OapaDriverCommands.StartupBatch(
                settings.OAPAXRunCurrent, settings.OAPAXHoldPercent,
                settings.OAPAYRunCurrent, settings.OAPAYHoldPercent,
                settings.OAPAXMicrosteps, settings.OAPAYMicrosteps)) {
                SendDriverCommand(command, "apply stored driver configuration");
            }

            // The motion parameters are not driver commands, so nothing else records them.
            // Logging them here makes a support log self-sufficient: the $J= step counts
            // that follow can be read back as arcminutes without guessing the ratio.
            Logger.Info(OapaParameterSummary.ForAxis("X (Azimuth)",
                settings.OAPAXGearRatio, settings.OAPAXBacklashCompensation,
                settings.OAPAXBacklashMode, settings.OAPAXSpeed,
                NegativeOrSame(settings.OAPAXBacklashCompensationNegative, settings.OAPAXBacklashCompensation),
                settings.OAPAXMicrosteps));
            Logger.Info(OapaParameterSummary.ForAxis("Y (Altitude)",
                settings.OAPAYGearRatio, settings.OAPAYBacklashCompensation,
                settings.OAPAYBacklashMode, settings.OAPAYSpeed,
                NegativeOrSame(settings.OAPAYBacklashCompensationNegative, settings.OAPAYBacklashCompensation),
                settings.OAPAYMicrosteps));
        }

        /// <summary>The firmware that runs the alignment itself answers the bridge commands.</summary>
        public const string AlignmentFirmwareVersion = "1.3.0";

        public bool RunsAlignment => FirmwareAtLeast(FirmwareVersion, AlignmentFirmwareVersion);

        internal static bool FirmwareAtLeast(string reported, string minimum) =>
            Version.TryParse(reported, out var version) && version >= Version.Parse(minimum);

        public string ForwardError(double azimuthArcmin, double altitudeArcmin) {
            SendToleranceIfChanged();
            return ExecuteWireCommand(string.Format(CultureInfo.InvariantCulture, "$E={0:F3},{1:F3}", azimuthArcmin, altitudeArcmin))?.Trim();
        }

        private double? sentTolerance;

        /// <summary>
        /// TPPA's tolerance is set in its own options and can change while connected; the
        /// controller has to stop at the same error, so a changed value goes ahead of the next
        /// reading. The controller only uses it while readings arrive, so that is always in time.
        /// A send that fails is retried with the next reading.
        /// </summary>
        private void SendToleranceIfChanged() {
            var tolerance = Properties.Settings.Default.AlignmentTolerance;
            if (sentTolerance == tolerance) {
                return;
            }
            try {
                var response = ExecuteWireCommand(string.Format(CultureInfo.InvariantCulture, "$T={0}", tolerance));
                sentTolerance = tolerance;
                Logger.Info($"OAPA controller: tolerance {tolerance}' sent (response: {response?.Trim()})");
            } catch (Exception ex) {
                Logger.Error($"OAPA controller: sending the tolerance failed: {ex.Message}");
            }
        }

        public string AlignmentStatus() => ExecuteWireCommand("$L?")?.Trim();

        public string RequestCalibration() => ExecuteWireCommand("$C=1")?.Trim();

        public string ControllerCalibrationStatus() => ExecuteWireCommand("$K?")?.Trim();

        public string RequestCalibrationOnly() => ExecuteWireCommand("$C=2")?.Trim();

        public string StopCalibration() => ExecuteWireCommand("$C=0")?.Trim();

        /// <summary>
        /// What the controller needs to run the alignment the way this plugin would: the steps
        /// per arcminute, each axis' backlash and mode, TPPA's tolerance, so both stop at the
        /// same error, and the largest single correction. Volatile on the controller like the driver settings, so pushed on
        /// every connection. Skipped on firmware that does not run the alignment.
        ///
        /// The factors are sent only when they were measured or typed in: factory defaults
        /// would only mislead the controller, which calibrates by itself when it has none.
        /// </summary>
        internal static string[] ControllerParameterCommands(Properties.Settings settings) {
            var c = CultureInfo.InvariantCulture;
            string Backlash(char axis, string mode, float positive, float negativeOrUnset) {
                var letter = mode switch { "Off" => 'O', "Soft" => 'S', "Unidirectional" => 'U', _ => 'F' };
                return string.Format(c, "$B={0},{1},{2},{3}", axis, letter, positive, NegativeOrSame(negativeOrUnset, positive));
            }
            var commands = new System.Collections.Generic.List<string>();
            if (settings.OAPAXGearRatioSource != nameof(OapaParameterSource.Default)
                && settings.OAPAYGearRatioSource != nameof(OapaParameterSource.Default)) {
                commands.Add(string.Format(c, "$F={0},{1}", settings.OAPAXGearRatio, settings.OAPAYGearRatio));
            }
            commands.AddRange(new[] {
                Backlash('X', settings.OAPAXBacklashMode, settings.OAPAXBacklashCompensation, settings.OAPAXBacklashCompensationNegative),
                Backlash('Y', settings.OAPAYBacklashMode, settings.OAPAYBacklashCompensation, settings.OAPAYBacklashCompensationNegative),
                string.Format(c, "$T={0}", settings.AlignmentTolerance),
                string.Format(c, "$M={0}", ClampMoveCap(settings.OAPAMoveCap)),
            });
            return commands.ToArray();
        }

        internal void ApplyControllerParameters() {
            if (!RunsAlignment) {
                return;
            }
            foreach (var command in ControllerParameterCommands(Properties.Settings.Default)) {
                SendDriverCommand(command, "push alignment parameters");
            }
        }

        /// <summary>The move cap the controller accepts ($M=), in arcminutes.</summary>
        internal static float ClampMoveCap(float arcmin) => Math.Clamp(arcmin, 1f, 120f);

        /// <summary>A stored value below zero means "never set": the axis is symmetric.</summary>
        private static float NegativeOrSame(float stored, float positive) {
            return stored < 0f ? positive : stored;
        }

        private void SendDriverCommand(string command, string what) {
            try {
                var response = ExecuteWireCommand(command);
                Logger.Info($"Driver config: {what} -> {command} (response: {response?.Trim()})");
            } catch (Exception ex) {
                Logger.Error($"Failed to {what} ({command}): {ex.Message}");
            }
        }

        // "!" decelerates both axes to a halt (firmware 1.2.1+); older firmware
        // replies a single "error" line and ignores it, so sending is always safe. The wire lock lets
        // this land between the status polls of a move in progress; the move's wait
        // loop then sees Idle short of target and exits gracefully.
        public override bool SupportsStop => true;

        public override void RequestStop() => TryRequestStop();

        /// <summary>
        /// Sends the halt and says whether it got through, which the shared signature cannot.
        ///
        /// A stop that does not reach the controller used to leave a log line and nothing else:
        /// the button was pressed, the platform kept moving, and the panel said the same thing
        /// it says on success, which is nothing. Of every control here this is the one whose
        /// whole purpose is to be trusted in a hurry.
        ///
        /// Reporting it belongs to the caller, not here: a transport that raises its own UI
        /// notifications is the shared-base complaint one layer down.
        /// </summary>
        public bool TryRequestStop() {
            try {
                var response = ExecuteWireCommand("!");
                Logger.Info($"Stop requested (response: {response?.Trim()})");
                return true;
            } catch (Exception ex) {
                Logger.Error($"Failed to request stop: {ex.Message}");
                return false;
            }
        }

        public void SetXRunCurrent(int currentMA) {
            SendDriverCommand(OapaDriverCommands.RunCurrent(Axis.XAxis, currentMA), "set X run current");
        }

        public void SetYRunCurrent(int currentMA) {
            SendDriverCommand(OapaDriverCommands.RunCurrent(Axis.YAxis, currentMA), "set Y run current");
        }

        public void SetXHoldPercent(int percent) {
            SendDriverCommand(OapaDriverCommands.HoldPercent(Axis.XAxis, percent), "set X hold percent");
        }

        public void SetYHoldPercent(int percent) {
            SendDriverCommand(OapaDriverCommands.HoldPercent(Axis.YAxis, percent), "set Y hold percent");
        }

        public void SetMicrosteps(Axis axis, int microsteps) {
            SendDriverCommand(OapaDriverCommands.Microsteps(axis, microsteps), $"set {axis} microsteps");
        }

        [GeneratedRegex(@"<(?<status>\w+)\|MPos:(?<x>[+-]?\d+(\.\d+)?),(?<y>[+-]?\d+(\.\d+)?),(?<z>[+-]?\d+(\.\d+)?)(?:\|T:(?<target>[+-]?\d+),R:(?<running>[01]),E:(?<endstop>[01]),S:(?<speed>[+-]?\d+(\.\d+)?))?(?:\|V:(?<version>\d+(?:\.\d+){0,2}))?\|>")]
        private static partial Regex StatusRegex();
    }
}