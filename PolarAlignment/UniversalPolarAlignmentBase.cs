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
        private readonly ISerialLink port;

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
        // Each retry costs (ScanReadTimeout + ScanWriteTimeout) on no-answer ports. None by
        // default: a system that does not override this probes each port once, as before.
        protected virtual int ConnectRetryAttempts => 0;
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

        private const float TargetPositionTolerance = 0.01f;
        private const double MovementTimeoutFactor = 2d;
        private static readonly TimeSpan MinimumMovementTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MovementTimeoutGracePeriod = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FallbackMovementTimeout = TimeSpan.FromSeconds(30);

        // Talks to an already-open link instead of scanning the COM ports. Runs the same
        // initial status probe as the scanning constructor below.
        protected UniversalPolarAlignmentBase(ISerialLink link) {
            port = link ?? throw new ArgumentNullException(nameof(link));
            UpdateStatus();
        }

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

                        var link = new SerialPortLink(serialPortToTest);
                        var matched = false;
                        for (var attempt = 0; attempt <= ConnectRetryAttempts && !matched; attempt++) {
                            try {
                                serialPortToTest.WriteLine("?");
                                var status = ReadStatusLine(link);
                                var match = GetStatusRegex().Match(status);
                                if (match.Success) {
                                    port = link;
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

        // The side of the play each axis sits on belongs to the mechanism, not to this
        // object: the alignment instruction builds a new one on every Execute while the
        // hardware keeps its position. See AxisEngagementState. Opt-in per system: one that
        // does not override this keeps the side on this object and assumes positive on every
        // new one, as before.
        protected virtual bool RemembersEngagementAcrossInstances => false;

        private string EngagementKey => GetType().Name;

        private LastDirection xLastDirection = LastDirection.Positive;
        private LastDirection yLastDirection = LastDirection.Positive;
        private LastDirection zLastDirection = LastDirection.Positive;

        public LastDirection XLastDirection => LastDirectionOf(Axis.XAxis);
        public LastDirection YLastDirection => LastDirectionOf(Axis.YAxis);
        public LastDirection ZLastDirection => LastDirectionOf(Axis.ZAxis);

        private LastDirection LastDirectionOf(Axis axis) {
            if (RemembersEngagementAcrossInstances) {
                return AxisEngagementState.Get(EngagementKey, axis);
            }
            return axis switch {
                Axis.XAxis => xLastDirection,
                Axis.YAxis => yLastDirection,
                _ => zLastDirection,
            };
        }

        private void RecordEngagement(Axis axis, float signedMove) {
            var direction = signedMove >= 0 ? LastDirection.Positive : LastDirection.Negative;
            if (RemembersEngagementAcrossInstances) {
                AxisEngagementState.Set(EngagementKey, axis, direction);
                return;
            }
            switch (axis) {
                case Axis.XAxis: xLastDirection = direction; break;
                case Axis.YAxis: yLastDirection = direction; break;
                default: zLastDirection = direction; break;
            }
        }

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

        // Serializes individual wire transactions (one command line + its reply). The
        // semaphore above serializes whole moves; this finer lock lets an out-of-band
        // command (the OAPA stop) be injected between the status polls of a move in
        // progress without corrupting the request/reply pairing on the port.
        private readonly object wireLock = new object();

        /// <summary>Bounded recovery schedule for a serial link that dies mid-session.</summary>
        private const int LinkReopenAttempts = 3;

        /// <summary>
        /// Base delay before a reopen attempt; attempt N waits N times this. The default
        /// schedule (1s, 2s, 3s) covers the typical USB re-enumeration window.
        /// </summary>
        protected virtual int LinkReopenDelayMs => 1000;

        private static bool IsLinkFailure(Exception ex)
            => ex is InvalidOperationException || ex is System.IO.IOException || ex is UnauthorizedAccessException;

        // The serial link can die mid-session - the field case is a USB re-enumeration
        // under the EMI of a stalling stepper. One transaction retry after a successful
        // reopen keeps a single dropout from failing the move and the whole correction
        // loop with it; a link that stays dead still throws to the caller. Runs under
        // wireLock, so the out-of-band stop waits out the recovery window - acceptable,
        // because a dead link cannot deliver a stop either.
        // Opt-in per system: one that does not override this lets a link failure reach the
        // caller on the first attempt, as before.
        protected virtual bool RecoversLink => false;

        private T WithLinkRecovery<T>(Func<T> transaction) {
            if (!RecoversLink) {
                return transaction();
            }
            try {
                return transaction();
            } catch (Exception ex) when (IsLinkFailure(ex)) {
                Logger.Warning($"{SystemName}: serial link failed ({ex.GetType().Name}: {ex.Message}); attempting to reopen the port");
                for (var attempt = 1; attempt <= LinkReopenAttempts; attempt++) {
                    Thread.Sleep(attempt * LinkReopenDelayMs);
                    if (port.TryReopen()) {
                        Logger.Info($"{SystemName}: serial link re-established (reopen attempt {attempt})");
                        return transaction();
                    }
                    Logger.Warning($"{SystemName}: reopen attempt {attempt}/{LinkReopenAttempts} failed");
                }
                throw;
            }
        }

        protected string ExecuteWireCommand(string command) {
            lock (wireLock) {
                return WithLinkRecovery(() => {
                    port.WriteLine(command);
                    return port.ReadLine();
                });
            }
        }

        // Stop support is opt-in per system so legacy controllers keep their exact
        // historical behavior. A system that overrides RequestStop must halt motion in
        // a way that keeps reported positions truthful.
        public virtual bool SupportsStop => false;

        public virtual void RequestStop() { }

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

                RecordEngagement(axis, position);

                var command = $"$J=G91G21{axisCommand}{commandedSteps.ToString(CultureInfo.InvariantCulture)}F{speed.ToString(CultureInfo.InvariantCulture)}";
                Logger.Info($"Sending command: {command}");
                var ok = ExecuteWireCommand(command);
                Logger.Info($"Response: {ok}");

                await WaitForMoveCompletion(checkProperty, target, speed, gearRatio, token);
            } finally {
                semaphore.Release();
            }
        }

        private async Task WaitForMoveCompletion(Func<float> checkProperty, float target, int speed, float gearRatio, CancellationToken token) {
            var startPos = checkProperty();
            var timeout = CalculateMovementTimeout(startPos, target, speed);
            var completionTol = CompletionToleranceSteps(gearRatio);
            var stuckTol = StuckDeltaSteps(gearRatio);
            var startTime = DateTime.Now;
            var lastPos = startPos;
            var stuckCount = 0;
            var idlePolls = 0;

            while (Math.Abs(checkProperty() - target) > completionTol) {
                UpdateStatus();
                var currentPos = checkProperty();

                // Stop-capable systems report Idle once motion has been halted (the
                // decelerating tail still reports Run). Two consecutive Idle polls short
                // of the target mean the move was ended externally — exit gracefully
                // instead of aging into the stuck/timeout exceptions below.
                if (SupportsStop && Status == "Idle") {
                    idlePolls++;
                    if (idlePolls >= 2) {
                        Logger.Info($"Move ended before reaching target (stopped): position {currentPos}, target was {target}");
                        return;
                    }
                } else {
                    idlePolls = 0;
                }

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

                var travel = axis switch {
                    Axis.XAxis => position - XPosition1,
                    Axis.YAxis => position - YPosition1,
                    _ => position - ZPosition1,
                };
                RecordEngagement(axis, travel);

                var command = $"$J=G53{axisCommand}{rawTarget.ToString(CultureInfo.InvariantCulture)}F{speed.ToString(CultureInfo.InvariantCulture)}";
                Logger.Info($"Sending command: {command}");
                var ok = ExecuteWireCommand(command);
                Logger.Info($"Response: {ok}");

                Func<float> checkProperty = axis switch {
                    Axis.XAxis => () => XPosition,
                    Axis.YAxis => () => YPosition,
                    Axis.ZAxis => () => ZPosition,
                    _ => throw new ArgumentException("Invalid Axis"),
                };

                await WaitForMoveCompletion(checkProperty, target, speed, gearRatio, token);
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
            string status;
            lock (wireLock) {
                status = WithLinkRecovery(() => {
                    port.WriteLine("?");
                    return ReadStatusLine(port);
                });
            }

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

        private static string ReadStatusLine(ISerialLink serialPort) {
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
