using NINA.Core.Utility;
using System;
using System.Text.RegularExpressions;

namespace NINA.Plugins.PolarAlignment.OAPA {
    public partial class UniversalPolarAlignmentOAPA : UniversalPolarAlignmentBase {
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

        public void SetXRunCurrent(int currentMA) {
            try {
                Port.WriteLine($"XC{currentMA}");
                Port.ReadLine();
            } catch (Exception ex) {
                Logger.Error($"Failed to set X run current: {ex.Message}");
            }
        }

        public void SetYRunCurrent(int currentMA) {
            try {
                Port.WriteLine($"YC{currentMA}");
                Port.ReadLine();
            } catch (Exception ex) {
                Logger.Error($"Failed to set Y run current: {ex.Message}");
            }
        }

        public void SetXHoldPercent(int percent) {
            try {
                Port.WriteLine($"XH{percent}");
                Port.ReadLine();
            } catch (Exception ex) {
                Logger.Error($"Failed to set X hold percent: {ex.Message}");
            }
        }

        public void SetYHoldPercent(int percent) {
            try {
                Port.WriteLine($"YH{percent}");
                Port.ReadLine();
            } catch (Exception ex) {
                Logger.Error($"Failed to set Y hold percent: {ex.Message}");
            }
        }

        [GeneratedRegex(@"<(?<status>\w+)\|MPos:(?<x>[+-]?\d+(\.\d+)?),(?<y>[+-]?\d+(\.\d+)?),(?<z>[+-]?\d+(\.\d+)?)(?:\|T:(?<target>[+-]?\d+),R:(?<running>[01]),E:(?<endstop>[01]),S:(?<speed>[+-]?\d+(\.\d+)?))?(?:\|V:(?<version>\d+(?:\.\d+){0,2}))?\|>")]
        private static partial Regex StatusRegex();
    }
}