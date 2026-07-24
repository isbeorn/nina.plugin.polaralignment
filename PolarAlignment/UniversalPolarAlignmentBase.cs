using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment {
    public abstract partial class UniversalPolarAlignmentBase : IPolarAlignmentSystem {
        private readonly SerialPort port;

        protected abstract string SystemName { get; }
        protected virtual string NewLineSequence => "\n";
        protected virtual int ScanReadTimeout => 1000;
        protected virtual int ScanWriteTimeout => 1000;
        protected virtual bool ClearBufferOnConnect => false;
        // Some boards (ESP32 on CH340/CP2102) auto-reset when the host opens the port and need
        // ~1–2 s before they can answer the status query. Override this to give the firmware
        // time to boot and emit its banner before the connection probe is sent.
        protected virtual int PostOpenDelayMs => 100;
        // How many extra status probes to attempt while waiting for the firmware to be ready.
        // Each retry costs (ScanReadTimeout + ScanWriteTimeout) on no-answer ports.
        protected virtual int ConnectRetryAttempts => 1;
        // Last-known-good port name; tried first to avoid scanning every COM. Override to
        // hook into a persisted user setting. Returning null/empty disables the shortcut.
        protected virtual string PreferredPortName => null;
        // Hook invoked after a successful match so derived systems can persist the matched
        // port name (e.g. into user settings) for the next connect.
        protected virtual void OnPortMatched(string portName) { }

        // Minimum firmware version this plugin build expects the device to report in its
        // status frame. Null disables the check for systems whose protocol has no version
        // field (e.g. Avalon UPAS).
        protected virtual string MinimumFirmwareVersion => null;
        // Where users can obtain the reference firmware; included in outdated-firmware warnings.
        protected virtual string FirmwareReferenceUrl => null;

        protected abstract Regex GetStatusRegex();

        protected SerialPort Port => port;

        private const float TargetPositionTolerance = 0.01f;
        private const double MovementTimeoutFactor = 2d;
        private static readonly TimeSpan MinimumMovementTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MovementTimeoutGracePeriod = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FallbackMovementTimeout = TimeSpan.FromSeconds(30);

        protected UniversalPolarAlignmentBase() {
            var allPorts = SerialPort.GetPortNames();
            var preferred = PreferredPortName;
            // Try preferred port first, then the rest. This collapses worst-case connect time
            // from O(N * timeout) to a single probe when the user reconnects to the same hardware.
            var ordered = !string.IsNullOrEmpty(preferred) && Array.IndexOf(allPorts, preferred) >= 0
                ? new[] { preferred }.Concat(allPorts.Where(p => p != preferred))
                : (System.Collections.Generic.IEnumerable<string>)allPorts;

            foreach (var comPort in ordered) {
                var serialPortToTest = new SerialPort() {
                    PortName = comPort,
                    BaudRate = 115200,
                    Parity = Parity.None,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    NewLine = NewLineSequence
                };

                serialPortToTest.ReadTimeout = ScanReadTimeout;
                serialPortToTest.WriteTimeout = ScanWriteTimeout;

                try {
                    serialPortToTest.Open();
                    if (serialPortToTest.IsOpen) {
                        if (ClearBufferOnConnect) {
                            try { serialPortToTest.DiscardInBuffer(); } catch { }
                        }

                        var matched = false;
                        for (var attempt = 0; attempt <= ConnectRetryAttempts && !matched; attempt++) {
                            try {
                                serialPortToTest.WriteLine("?");
                                var status = ReadStatusLine(serialPortToTest);
                                var match = GetStatusRegex().Match(status);
                                if (match.Success) {
                                    port = serialPortToTest;
                                    Logger.Info($"Found {SystemName} on {comPort}");
                                    OnPortMatched(comPort);
                                    matched = true;
                                    break;
                                }
                                Logger.Debug($"{SystemName} probe on {comPort} attempt {attempt + 1}: unrecognised response '{status}'");
                            } catch (TimeoutException) {
                                Logger.Debug($"{SystemName} probe on {comPort} attempt {attempt + 1} timed out");
                            }
                            // If we still have retries left, give the device more time to boot/settle.
                            if (attempt < ConnectRetryAttempts) {
                                Thread.Sleep(PostOpenDelayMs);
                                try { serialPortToTest.DiscardInBuffer(); } catch { }
                            }
                        }
                        if (matched) {
                            break;
                        }
                        serialPortToTest.Close();
                        serialPortToTest.Dispose();
                        continue;
                    }
                } catch {
                    serialPortToTest?.Close();
                    serialPortToTest?.Dispose();
                }
            }
            if (port == null) {
                throw new Exception($"Unable to find {SystemName}");
            }
            UpdateStatus();
        }

        public bool Connected => port.IsOpen;
        public string Status { get; private set; }
        public string FirmwareVersion { get; private set; }

        private float XPosition { get; set; }
        private float YPosition { get; set; }
        private float ZPosition { get; set; }

        public LastDirection XLastDirection { get; private set; } = LastDirection.Positive;
        public LastDirection YLastDirection { get; private set; } = LastDirection.Positive;
        public LastDirection ZLastDirection { get; private set; } = LastDirection.Positive;

        public float XPosition1 { get => XPosition / XGearRatio; }
        public float YPosition1 { get => YPosition / YGearRatio; }
        public float ZPosition1 { get => ZPosition / ZGearRatio; }

        public abstract float XGearRatio { get; set; }
        public abstract float YGearRatio { get; set; }
        public float ZGearRatio { get; set; } = 1;

        // Virtual hooks for derived systems to customize motion completion behavior
        // without affecting other implementations. Defaults preserve historical behavior
        // (Avalon and any system that does not override remains unchanged).
        protected virtual float CompletionToleranceSteps(float gearRatio) => 0.01f;
        protected virtual float StuckDeltaSteps(float gearRatio) => 0.01f;
        protected virtual float RoundTarget(float target) => target;

        private SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);

        public async Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
            await semaphore.WaitAsync(token);
            try {
                UpdateStatus();
                var axisCommand = axis switch {
                    Axis.XAxis => "X",
                    Axis.YAxis => "Y",
                    Axis.ZAxis => "Z",
                    _ => throw new ArgumentException("Invalid Axis"),
                };
                var gearRatio = axis switch {
                    Axis.XAxis => XGearRatio,
                    Axis.YAxis => YGearRatio,
                    Axis.ZAxis => ZGearRatio,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                Func<float> checkProperty = axis switch {
                    Axis.XAxis => () => XPosition,
                    Axis.YAxis => () => YPosition,
                    Axis.ZAxis => () => ZPosition,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                var commandedSteps = position * gearRatio;
                var target = RoundTarget(checkProperty() + commandedSteps);

                switch (axis) {
                    case Axis.XAxis: XLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.YAxis: YLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.ZAxis: ZLastDirection = position >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                }

                var command = $"$J=G91G21{axisCommand}{commandedSteps.ToString(CultureInfo.InvariantCulture)}F{speed.ToString(CultureInfo.InvariantCulture)}";
                Logger.Info($"Sending command: {command}");
                port.WriteLine(command);
                var ok = port.ReadLine();
                Logger.Info($"Response: {ok}");

                var startPos = checkProperty();
                var timeout = CalculateMovementTimeout(startPos, target, speed);
                var completionTol = CompletionToleranceSteps(gearRatio);
                var stuckTol = StuckDeltaSteps(gearRatio);
                var startTime = DateTime.Now;
                var lastPos = startPos;
                var stuckCount = 0;

                while (Math.Abs(checkProperty() - target) > completionTol) {
                    UpdateStatus();
                    var currentPos = checkProperty();

                    if (Math.Abs(currentPos - lastPos) < stuckTol) {
                        stuckCount++;
                        if (stuckCount > 5) {
                            throw new TimeoutException($"Motor appears stuck at position {currentPos}. Target was {target}. Check hardware and endstops.");
                        }
                    } else {
                        stuckCount = 0;
                    }
                    lastPos = currentPos;

                    if (DateTime.Now - startTime > timeout) {
                        throw new TimeoutException($"Movement timeout after {timeout.TotalSeconds:N1}s. Current: {currentPos}, Target: {target}");
                    }

                    await Task.Delay(300, token);
                }
            } finally {
                semaphore.Release();
            }
        }

        public async Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
            await semaphore.WaitAsync(token);
            try {
                UpdateStatus();
                var axisCommand = axis switch {
                    Axis.XAxis => "X",
                    Axis.YAxis => "Y",
                    Axis.ZAxis => "Z",
                    _ => throw new ArgumentException("Invalid Axis"),
                };
                var gearRatio = axis switch {
                    Axis.XAxis => XGearRatio,
                    Axis.YAxis => YGearRatio,
                    Axis.ZAxis => ZGearRatio,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                var rawTarget = position * gearRatio;
                var target = RoundTarget(rawTarget);

                switch (axis) {
                    case Axis.XAxis: XLastDirection = position - XPosition1 >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.YAxis: YLastDirection = position - YPosition1 >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                    case Axis.ZAxis: ZLastDirection = position - ZPosition1 >= 0 ? LastDirection.Positive : LastDirection.Negative; break;
                }

                var command = $"$J=G53{axisCommand}{rawTarget.ToString(CultureInfo.InvariantCulture)}F{speed.ToString(CultureInfo.InvariantCulture)}";
                Logger.Info($"Sending command: {command}");
                port.WriteLine(command);
                var ok = port.ReadLine();
                Logger.Info($"Response: {ok}");

                Func<float> checkProperty = axis switch {
                    Axis.XAxis => () => XPosition,
                    Axis.YAxis => () => YPosition,
                    Axis.ZAxis => () => ZPosition,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                var startPos = checkProperty();
                var timeout = CalculateMovementTimeout(startPos, target, speed);
                var completionTol = CompletionToleranceSteps(gearRatio);
                var stuckTol = StuckDeltaSteps(gearRatio);
                var startTime = DateTime.Now;
                var lastPos = startPos;
                var stuckCount = 0;

                while (Math.Abs(checkProperty() - target) > completionTol) {
                    UpdateStatus();
                    var currentPos = checkProperty();

                    if (Math.Abs(currentPos - lastPos) < stuckTol) {
                        stuckCount++;
                        if (stuckCount > 5) {
                            throw new TimeoutException($"Motor appears stuck at position {currentPos}. Target was {target}. Check hardware and endstops.");
                        }
                    } else {
                        stuckCount = 0;
                    }
                    lastPos = currentPos;

                    if (DateTime.Now - startTime > timeout) {
                        throw new TimeoutException($"Movement timeout after {timeout.TotalSeconds:N1}s. Current: {currentPos}, Target: {target}");
                    }

                    await Task.Delay(300, token);
                }
            } finally {
                semaphore.Release();
            }
        }

        internal static TimeSpan CalculateMovementTimeout(float startPosition, float targetPosition, int speed) {
            var distance = Math.Abs(targetPosition - startPosition);
            if (distance <= TargetPositionTolerance) {
                return MinimumMovementTimeout;
            }
            if (speed <= 0) {
                return FallbackMovementTimeout;
            }

            var expectedSeconds = distance / speed * 60d;
            var timeout = TimeSpan.FromSeconds(expectedSeconds * MovementTimeoutFactor) + MovementTimeoutGracePeriod;
            return timeout > MinimumMovementTimeout ? timeout : MinimumMovementTimeout;
        }

        private void UpdateStatus() {
            port.WriteLine("?");
            var status = ReadStatusLine(port);

            var match = GetStatusRegex().Match(status);
            if (match.Success) {
                Status = match.Groups["status"].Value;
                XPosition = float.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
                YPosition = float.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
                ZPosition = float.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture);
                var versionGroup = match.Groups["version"];
                FirmwareVersion = versionGroup.Success ? versionGroup.Value : null;
                CheckFirmwareVersion();
            } else {
                Logger.Error($"Failed to parse {SystemName} status: {status}");
            }
        }

        private bool firmwareVersionChecked;

        private void CheckFirmwareVersion() {
            if (firmwareVersionChecked || MinimumFirmwareVersion == null) {
                return;
            }
            firmwareVersionChecked = true;

            var referenceHint = FirmwareReferenceUrl == null ? string.Empty : $" The reference firmware is available at {FirmwareReferenceUrl}";
            if (!Version.TryParse(NormalizeVersion(FirmwareVersion), out var reported)) {
                Logger.Warning($"{SystemName} firmware does not report a version; version {MinimumFirmwareVersion} or newer is recommended.{referenceHint}");
                Notification.ShowWarning($"{SystemName}: the connected firmware does not report a version. Updating to firmware {MinimumFirmwareVersion} or newer is recommended.");
                return;
            }
            if (Version.TryParse(NormalizeVersion(MinimumFirmwareVersion), out var minimum) && reported < minimum) {
                Logger.Warning($"{SystemName} firmware {FirmwareVersion} is older than the recommended {MinimumFirmwareVersion}.{referenceHint}");
                Notification.ShowWarning($"{SystemName}: firmware {FirmwareVersion} is older than the recommended {MinimumFirmwareVersion}. Consider updating.");
                return;
            }
            Logger.Info($"{SystemName} firmware version: {FirmwareVersion}");
        }

        // Version.TryParse needs at least "major.minor"; firmware may report a bare major.
        private static string NormalizeVersion(string version) {
            if (string.IsNullOrWhiteSpace(version)) {
                return null;
            }
            return version.Contains('.') ? version : version + ".0";
        }

        private static string ReadStatusLine(SerialPort serialPort) {
            var status = serialPort.ReadLine();
            if (string.IsNullOrWhiteSpace(status) ||
                string.Equals(status.Trim(), "ok", StringComparison.OrdinalIgnoreCase)) {
                status = serialPort.ReadLine();
            } else {
                _ = serialPort.ReadLine();
            }
            return status;
        }

        public async Task RefreshStatus(CancellationToken token) {
            await semaphore.WaitAsync(token);
            try {
                UpdateStatus();
            } finally {
                semaphore.Release();
            }
        }

        public void Dispose() => port?.Dispose();
    }
}
