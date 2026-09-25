using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Bridge {

    /// <summary>
    /// Tunables of the external correction protocol. They are fixed in the plugin on purpose: TPPA
    /// publishes them in its <c>Capabilities</c> reply so the controller never has to hard code them,
    /// and the user needs no setting because the mode is detected automatically.
    /// </summary>
    public sealed class BridgeOptions {
        public const int DefaultHeartbeatMs = 2000;
        public const int DefaultSilenceTimeoutMs = 15000;
        public const int DefaultReadyTimeoutMs = 30000;
        public const int DefaultGraceAfterSilenceMs = 10000;
        public const int DefaultSessionTimeoutSec = 1800;
        public const int DefaultStopAckTimeoutMs = 5000;

        public int HeartbeatMs { get; set; } = DefaultHeartbeatMs;
        public int SilenceTimeoutMs { get; set; } = DefaultSilenceTimeoutMs;

        /// <summary>How long TPPA waits for the controller to report that its hardware is ready.</summary>
        public int ReadyTimeoutMs { get; set; } = DefaultReadyTimeoutMs;

        /// <summary>Extra time before a silent controller is treated as gone.</summary>
        public int GraceAfterSilenceMs { get; set; } = DefaultGraceAfterSilenceMs;

        /// <summary>
        /// Safety limit of a single external session. It only counts the time the controller is NOT
        /// holding a capture window, so a long chain of moves can never trip it.
        /// </summary>
        public int SessionTimeoutSec { get; set; } = DefaultSessionTimeoutSec;

        public int StopAckTimeoutMs { get; set; } = DefaultStopAckTimeoutMs;
        public double ToleranceArcMin { get; set; }
        public bool ContinuousEstimation { get; set; }
    }

    /// <summary>The next controller-side event the alignment loop has to act on.</summary>
    public sealed class BridgeRequest {
        public BridgeRequestKind Kind { get; set; }
        public BridgeEnvelope Envelope { get; set; }
        public string WindowId { get; set; }
        public string MeasurementId { get; set; }

        /// <summary>True when the same command was already served and must not be executed twice.</summary>
        public bool Duplicate { get; set; }

        public BridgeAdjustmentRequestPayload Adjustment { get; set; }
        public BridgeMeasurementRequestPayload MeasurementRequest { get; set; }
        public BridgeCompletionRequestPayload CompletionRequest { get; set; }
        public BridgeCancelPayload Cancel { get; set; }
        public BridgeFaultPayload Fault { get; set; }
    }

    /// <summary>
    /// One external correction session: owns the capture window, the silence watchdog, the
    /// heartbeat and the message bookkeeping. It is deliberately free of camera/mount access so the
    /// alignment loop stays in <see cref="Instructions.PolarAlignment"/> and this class stays testable.
    /// </summary>
    public sealed class BridgeSession : IDisposable {
        private readonly IMessageBroker broker;
        private readonly object gate = new object();
        private readonly Queue<BridgeRequest> pending = new Queue<BridgeRequest>();
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        private readonly Dictionary<string, string> grantedWindowByCommandId = new Dictionary<string, string>();
        private readonly HashSet<string> servedMeasurementRequests = new HashSet<string>();
        private readonly HashSet<string> processedCommandIds = new HashSet<string>();

        private readonly DateTimeOffset startedAt;
        private readonly Timer heartbeatTimer;

        private long sequence;
        private int samplesTaken;
        private string lastMeasurementId;
        private string lastMeasurementWindowId;
        private bool lastMeasurementBelowTolerance;
        private int consecutiveBelowTolerance;

        private string state = BridgeState.Preparing;
        private string stateReason;
        private DateTimeOffset? windowOpenedAt;
        private DateTimeOffset? windowSilenceDeadline;
        private DateTimeOffset? windowMaxDeadline;
        private string windowId;
        private string windowMeasurementId;
        private TimeSpan accumulatedWindowTime;
        private DateTimeOffset? lostAfterSilenceDeadline;
        private bool stopRequested;
        private bool stopAcknowledged;
        private bool terminalRequestPending;
        private string hardwareStopStatus;
        private bool explicitOptions;
        private bool disposed;

        /// <summary>
        /// True while a request that ends the session is waiting in the queue. The paused loop uses it to
        /// keep watching for a controller that gives up or disappears, without consuming the ordinary
        /// requests the resumed loop still needs.
        /// </summary>
        public bool HasPendingTerminalRequest {
            get { lock (gate) { return terminalRequestPending; } }
        }

        public BridgeSession(IMessageBroker broker, string sessionId = null) {
            this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
            Options = new BridgeOptions();
            startedAt = DateTimeOffset.UtcNow;
            heartbeatTimer = new Timer(OnHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
        }

        public string SessionId { get; }

        public BridgeOptions Options { get; private set; }

        /// <summary>
        /// Applies explicit tunables instead of reading them from the plugin settings. The alignment
        /// loop and the tests use this so both sides agree on the values published in the protocol.
        /// </summary>
        public void ApplyOptions(BridgeOptions options) {
            if (options == null) { return; }
            Options = options;
            explicitOptions = true;
        }

        public bool IsActive => !disposed && state != BridgeState.Ended;

        public string State => state;

        public bool IsWindowOpen => windowId != null;

        public string CurrentWindowId => windowId;

        public int SamplesTaken => samplesTaken;

        /// <summary>Wall time the session has been running, excluding the time capture windows were open.</summary>
        public TimeSpan RealRunningElapsed {
            get {
                var now = DateTimeOffset.UtcNow;
                var windowTime = accumulatedWindowTime;
                if (windowOpenedAt.HasValue) {
                    windowTime += now - windowOpenedAt.Value;
                }
                var total = now - startedAt - windowTime;
                return total < TimeSpan.Zero ? TimeSpan.Zero : total;
            }
        }

        /// <summary>Publishes the session opening state and starts the heartbeat.</summary>
        public async Task OpenAsync(double toleranceArcMin, bool continuousEstimation, CancellationToken token) {
            if (!explicitOptions) {
                Options = new BridgeOptions();
            }
            Options.ToleranceArcMin = toleranceArcMin;
            Options.ContinuousEstimation = continuousEstimation;

            state = BridgeState.Preparing;
            stateReason = null;
            heartbeatTimer.Change(Options.HeartbeatMs, Options.HeartbeatMs);
            await PublishSessionStateAsync(state, null, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Blocks until the controller confirms that its hardware is connected and idle. Reaching this
        /// point already means a controller is present (it announces itself only while it is enabled),
        /// so this second step is what tells TPPA that the hardware is really usable.
        /// </summary>
        public async Task<bool> WaitForControllerReadyAsync(CancellationToken token) {
            if (state == BridgeState.Preparing) {
                await PublishSessionStateAsync(state, null, token).ConfigureAwait(false);
            }

            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.ReadyTimeoutMs);
            while (true) {
                token.ThrowIfCancellationRequested();
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) {
                    Logger.Warning("[Bridge] No controller ready message received within the ready timeout.");
                    return false;
                }

                var request = await WaitForControllerRequestAsync(token, remaining).ConfigureAwait(false);
                if (request == null) { continue; }

                if (request.Kind == BridgeRequestKind.ControllerReady) { return true; }
                if (request.Kind == BridgeRequestKind.Cancel) { return false; }
                if (request.Kind == BridgeRequestKind.SessionTimeout
                    || request.Kind == BridgeRequestKind.ExternalLost) { return false; }

                Logger.Debug($"[Bridge] Ignoring '{request.Kind}' while waiting for the controller to become ready.");
            }
        }

        /// <summary>
        /// Waits for the next controller request. Returns TPPA-side events (window expired, session
        /// lost, session timeout) when a deadline is hit instead of a message.
        /// </summary>
        public Task<BridgeRequest> WaitForControllerRequestAsync(CancellationToken token) {
            return WaitForControllerRequestAsync(token, null);
        }

        private async Task<BridgeRequest> WaitForControllerRequestAsync(CancellationToken token, TimeSpan? maxWait) {
            var hardDeadline = maxWait.HasValue ? DateTimeOffset.UtcNow + maxWait.Value : (DateTimeOffset?)null;

            while (true) {
                token.ThrowIfCancellationRequested();

                lock (gate) {
                    if (pending.Count > 0) {
                        return pending.Dequeue();
                    }
                }

                var now = DateTimeOffset.UtcNow;
                var wait = Timeout.InfiniteTimeSpan;

                if (hardDeadline.HasValue) {
                    wait = Min(wait, hardDeadline.Value - now);
                    if (wait <= TimeSpan.Zero) { return null; }
                }

                if (lostAfterSilenceDeadline.HasValue) {
                    if (now >= lostAfterSilenceDeadline.Value) {
                        lostAfterSilenceDeadline = null;
                        Logger.Warning("[Bridge] Controller stayed silent after the capture window expired.");
                        return new BridgeRequest { Kind = BridgeRequestKind.ExternalLost };
                    }
                    wait = Min(wait, lostAfterSilenceDeadline.Value - now);
                }

                var sessionRemaining = TimeSpan.FromSeconds(Options.SessionTimeoutSec) - RealRunningElapsed;
                if (sessionRemaining <= TimeSpan.Zero) {
                    Logger.Warning("[Bridge] Session reached the safety time limit.");
                    return new BridgeRequest { Kind = BridgeRequestKind.SessionTimeout };
                }
                wait = Min(wait, sessionRemaining);

                if (windowSilenceDeadline.HasValue) {
                    if (now >= windowSilenceDeadline.Value) {
                        CloseWindow(BridgeReason.SilenceTimeout);
                        await PublishSessionStateAsync(state, BridgeReason.SilenceTimeout, token).ConfigureAwait(false);
                        lostAfterSilenceDeadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.GraceAfterSilenceMs);
                        return new BridgeRequest { Kind = BridgeRequestKind.WindowExpired };
                    }
                    wait = Min(wait, windowSilenceDeadline.Value - now);
                }

                if (wait == Timeout.InfiniteTimeSpan) {
                    wait = TimeSpan.FromSeconds(5);
                }

                var milliseconds = (int)Math.Min(int.MaxValue, Math.Max(1, wait.TotalMilliseconds));
                await signal.WaitAsync(milliseconds, token).ConfigureAwait(false);
            }
        }

        private static TimeSpan Min(TimeSpan current, TimeSpan candidate) {
            if (candidate < TimeSpan.Zero) { return TimeSpan.Zero; }
            return candidate < current ? candidate : current;
        }

        /// <summary>Publishes the current polar error as a measurement so the controller can plan a move.</summary>
        public async Task PublishMeasurementAsync(BridgeMeasurementPayload payload, CancellationToken token) {
            if (payload == null) { return; }

            lock (gate) {
                samplesTaken++;
                lastMeasurementId = payload.MeasurementId;
                lastMeasurementWindowId = payload.WindowId;
                lastMeasurementBelowTolerance = payload.ToleranceReached;
                consecutiveBelowTolerance = payload.ConsecutiveBelowTolerance;
                state = BridgeState.WaitingForRequest;
                stateReason = null;
            }

            payload.SessionId = SessionId;
            payload.SampleIndex = samplesTaken;
            payload.ToleranceArcMin = Options.ToleranceArcMin;
            payload.ContinuousEstimation = Options.ContinuousEstimation;

            await PublishAsync(BridgeKind.Measurement, payload, null, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Grants a capture window for the adjustment announced by the controller and answers with
        /// <c>AdjustmentGranted</c>. Returns the granted window id.
        /// </summary>
        public async Task<string> GrantWindowAsync(BridgeRequest request, CancellationToken token) {
            if (request == null) { return null; }

            var cached = TryGetCachedGrant(request.Envelope?.CommandId);
            if (cached != null) {
                await PublishAdjustmentGrantedAsync(cached, request.Adjustment?.MeasurementId, token).ConfigureAwait(false);
                return cached;
            }

            var sessionRemaining = TimeSpan.FromSeconds(Options.SessionTimeoutSec) - RealRunningElapsed;
            var grant = new BridgeAdjustmentGrantPayload {
                WindowId = Guid.NewGuid().ToString("N"),
                MeasurementId = request.Adjustment?.MeasurementId,
                MaxWindowMs = (long)Math.Max(0, sessionRemaining.TotalMilliseconds),
                SilenceTimeoutMs = Options.SilenceTimeoutMs,
                GrantedAtUtc = DateTimeOffset.UtcNow
            };

            lock (gate) {
                if (windowId != null) {
                    accumulatedWindowTime += DateTimeOffset.UtcNow - (windowOpenedAt ?? DateTimeOffset.UtcNow);
                }
                windowId = grant.WindowId;
                windowMeasurementId = grant.MeasurementId;
                windowOpenedAt = DateTimeOffset.UtcNow;
                windowSilenceDeadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.SilenceTimeoutMs);
                windowMaxDeadline = Options.SessionTimeoutSec > 0 ? DateTimeOffset.UtcNow + sessionRemaining : (DateTimeOffset?)null;
                lostAfterSilenceDeadline = null;
                state = BridgeState.WindowOpen;
                stateReason = null;
                RememberGrant(request.Envelope?.CommandId, grant.WindowId);
            }

            await PublishAdjustmentGrantedAsync(grant.WindowId, grant.MeasurementId, token).ConfigureAwait(false);
            return grant.WindowId;
        }

        private async Task PublishAdjustmentGrantedAsync(string grantedWindowId, string measurementId, CancellationToken token) {
            var payload = new BridgeAdjustmentGrantPayload {
                WindowId = grantedWindowId,
                MeasurementId = measurementId,
                MaxWindowMs = (long)Math.Max(0, (TimeSpan.FromSeconds(Options.SessionTimeoutSec) - RealRunningElapsed).TotalMilliseconds),
                SilenceTimeoutMs = Options.SilenceTimeoutMs,
                GrantedAtUtc = DateTimeOffset.UtcNow
            };
            await PublishAsync(BridgeKind.AdjustmentGranted, payload, null, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Closes the current capture window because the controller asked for a new measurement.
        /// The silence watchdog and the heartbeat stay active.
        /// </summary>
        public void CloseWindow(string reason) {
            lock (gate) {
                if (windowId == null) { return; }
                if (windowOpenedAt.HasValue) {
                    accumulatedWindowTime += DateTimeOffset.UtcNow - windowOpenedAt.Value;
                }
                windowId = null;
                windowMeasurementId = null;
                windowOpenedAt = null;
                windowSilenceDeadline = null;
                windowMaxDeadline = null;
                lostAfterSilenceDeadline = null;
                state = BridgeState.Measuring;
                stateReason = reason;
            }
        }

        /// <summary>Marks the loop as being in the verification phase requested by the controller.</summary>
        public void EnterVerifying() {
            lock (gate) {
                state = BridgeState.Verifying;
                stateReason = BridgeReason.CompletionRequested;
            }
        }

        /// <summary>Marks the loop as capturing the three reference points.</summary>
        public void EnterMeasuring(string reason) {
            lock (gate) {
                state = BridgeState.Measuring;
                stateReason = reason;
            }
        }

        public Task PublishSessionStateAsync(string newState, string reason, CancellationToken token) {
            lock (gate) {
                if (newState != null) { state = newState; }
                if (reason != null) { stateReason = reason; }
            }
            return PublishSessionStateCoreAsync(token);
        }

        /// <summary>
        /// Tells the controller that the operator paused or resumed the run. The controller stops a move
        /// that is in progress; TPPA does not wait for an answer, because a pause has to reach a
        /// controller that is busy inside a move.
        /// </summary>
        public Task PublishPauseRequestAsync(bool paused, string reason, CancellationToken token) {
            var payload = new BridgePauseRequestPayload {
                Paused = paused,
                Reason = reason
            };
            return PublishAsync(BridgeKind.PauseRequested, payload, null, token);
        }

        private Task PublishSessionStateCoreAsync(CancellationToken token) {
            BridgeSessionStatePayload payload;
            lock (gate) {
                payload = new BridgeSessionStatePayload {
                    State = state,
                    Reason = stateReason,
                    WindowId = windowId,
                    WindowRemainingMs = windowSilenceDeadline.HasValue
                        ? (long)Math.Max(0, (windowSilenceDeadline.Value - DateTimeOffset.UtcNow).TotalMilliseconds)
                        : 0,
                    SamplesTaken = samplesTaken,
                    LastMeasurementId = lastMeasurementId,
                    ToleranceArcMin = Options.ToleranceArcMin,
                    SessionElapsedMs = (long)RealRunningElapsed.TotalMilliseconds
                };
            }
            return PublishAsync(BridgeKind.SessionState, payload, null, token);
        }

        /// <summary>Asks the controller to stop and waits for its acknowledgement within the ack timeout.</summary>
        public async Task<string> RequestStopAsync(string reason, CancellationToken token) {
            lock (gate) {
                if (stopRequested) { return hardwareStopStatus; }
                stopRequested = true;
                stopAcknowledged = false;
            }

            var payload = new BridgeStopRequestPayload {
                Reason = reason,
                AckTimeoutMs = Options.StopAckTimeoutMs
            };
            await PublishAsync(BridgeKind.StopRequested, payload, null, token).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.StopAckTimeoutMs);
            while (DateTimeOffset.UtcNow < deadline) {
                lock (gate) {
                    if (stopAcknowledged) { return hardwareStopStatus; }
                }
                try {
                    await Task.Delay(50, token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
            }

            Logger.Warning($"[Bridge] Controller did not acknowledge the stop request within {Options.StopAckTimeoutMs} ms.");
            return BridgeHardwareStopStatus.Unknown;
        }

        /// <summary>Publishes the final message of the session.</summary>
        public async Task EndAsync(string reason, bool achieved, BridgeSessionEndedPayload detail, CancellationToken token) {
            TimeSpan elapsed;
            lock (gate) {
                CloseWindow(reason);
                state = BridgeState.Ended;
                stateReason = reason;
                heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
                elapsed = RealRunningElapsed;
            }

            var payload = detail ?? new BridgeSessionEndedPayload();
            payload.Reason = reason;
            payload.Achieved = achieved;
            payload.ToleranceUsedArcMin = Options.ToleranceArcMin;
            if (payload.SamplesUsed == 0) { payload.SamplesUsed = samplesTaken; }
            if (string.IsNullOrEmpty(payload.HardwareStopStatus)) {
                payload.HardwareStopStatus = hardwareStopStatus ?? BridgeHardwareStopStatus.Unknown;
            }

            Logger.Info($"[Bridge] Session {SessionId} ended. Reason: {reason}; achieved: {achieved}; samples: {payload.SamplesUsed}; elapsed: {elapsed:hh\\:mm\\:ss}.");
            await PublishAsync(BridgeKind.SessionEnded, payload, null, token).ConfigureAwait(false);
        }

        /// <summary>Remembered values of the last published measurement (used by the verification policy).</summary>
        public (string MeasurementId, string WindowId, bool BelowTolerance, int ConsecutiveBelowTolerance) LastMeasurement {
            get {
                lock (gate) {
                    return (lastMeasurementId, lastMeasurementWindowId, lastMeasurementBelowTolerance, consecutiveBelowTolerance);
                }
            }
        }

        /// <summary>Feeds one inbound controller message into the session.</summary>
        public void HandleEnvelope(BridgeEnvelope envelope) {
            if (envelope == null || disposed) { return; }

            if (envelope.Version != BridgeContract.InterfaceVersion) {
                Logger.Warning($"[Bridge] Ignoring controller message with interface version {envelope.Version}.");
                return;
            }

            if (!string.IsNullOrEmpty(envelope.IntendedRecipient)
                && !string.Equals(envelope.IntendedRecipient, BridgeContract.TppaRecipient, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            if (!string.IsNullOrEmpty(envelope.SessionId)
                && !string.Equals(envelope.SessionId, SessionId, StringComparison.Ordinal)) {
                Logger.Warning($"[Bridge] Ignoring controller message for session {envelope.SessionId}.");
                return;
            }

            switch (envelope.Kind) {
                case BridgeKind.KeepAlive:
                    RefreshSilenceDeadline(envelope);
                    return;

                case BridgeKind.Stopped:
                    lock (gate) {
                        stopAcknowledged = true;
                        var stopped = envelope.PayloadAs<BridgeStoppedPayload>();
                        hardwareStopStatus = string.IsNullOrEmpty(stopped?.HardwareStopStatus)
                            ? BridgeHardwareStopStatus.Ok
                            : stopped.HardwareStopStatus;
                    }
                    Enqueue(new BridgeRequest {
                        Kind = BridgeRequestKind.StopAcknowledged,
                        Envelope = envelope
                    });
                    return;

                case BridgeKind.ControllerReady:
                    if (!TryAccept(envelope.CommandId)) { return; }
                    Enqueue(new BridgeRequest {
                        Kind = BridgeRequestKind.ControllerReady,
                        Envelope = envelope
                    });
                    return;

                case BridgeKind.BeginAdjustment: {
                    var adjustment = envelope.PayloadAs<BridgeAdjustmentRequestPayload>();
                    if (!TryAccept(envelope.CommandId)) {
                        // Idempotent repeat: answer with the window that this command already got.
                        var cachedWindow = TryGetCachedGrant(envelope.CommandId);
                        if (cachedWindow != null) {
                            _ = PublishAdjustmentGrantedAsync(cachedWindow, adjustment?.MeasurementId, CancellationToken.None);
                        }
                        return;
                    }
                    Enqueue(new BridgeRequest {
                        Kind = BridgeRequestKind.AdjustWindow,
                        Envelope = envelope,
                        MeasurementId = adjustment?.MeasurementId,
                        Adjustment = adjustment
                    });
                    return;
                }

                case BridgeKind.RequestMeasurement: {
                    var payload = envelope.PayloadAs<BridgeMeasurementRequestPayload>();
                    var duplicate = !TryAccept(envelope.CommandId);
                    if (!duplicate) {
                        // "Close the window and capture now" is a protocol rule, so the session applies
                        // it as soon as the request arrives instead of leaving it to the caller loop.
                        CloseWindow(payload?.Reason ?? BridgeReason.StepFinished);
                        lock (gate) {
                            servedMeasurementRequests.Add(envelope.CommandId);
                        }
                    }
                    Enqueue(new BridgeRequest {
                        Kind = BridgeRequestKind.RequestMeasurement,
                        Envelope = envelope,
                        Duplicate = duplicate,
                        WindowId = payload?.WindowId,
                        MeasurementRequest = payload
                    });
                    return;
                }

                case BridgeKind.RequestCompletion: {
                    var completion = envelope.PayloadAs<BridgeCompletionRequestPayload>();
                    if (!TryAccept(envelope.CommandId)) { return; }
                    // A completion request while a window is open closes it and verifies instead of
                    // being rejected, so the controller can always stop the correction chain.
                    CloseWindow(BridgeReason.CompletionRequested);
                    Enqueue(new BridgeRequest {
                        Kind = BridgeRequestKind.RequestCompletion,
                        Envelope = envelope,
                        WindowId = completion?.WindowId,
                        CompletionRequest = completion
                    });
                    return;
                }

                case BridgeKind.Cancel:
                    if (!TryAccept(envelope.CommandId)) { return; }
                    Enqueue(new BridgeRequest {
                        Kind = BridgeRequestKind.Cancel,
                        Envelope = envelope,
                        Cancel = envelope.PayloadAs<BridgeCancelPayload>()
                    });
                    return;

                case BridgeKind.Fault: {
                    var fault = envelope.PayloadAs<BridgeFaultPayload>();
                    lock (gate) {
                        hardwareStopStatus = string.IsNullOrEmpty(fault?.HardwareStopStatus)
                            ? BridgeHardwareStopStatus.Unknown
                            : fault.HardwareStopStatus;
                    }
                    Enqueue(new BridgeRequest {
                        Kind = BridgeRequestKind.Cancel,
                        Envelope = envelope,
                        Fault = fault,
                        Cancel = new BridgeCancelPayload {
                            Reason = string.IsNullOrEmpty(fault?.Reason)
                                ? BridgeReason.ControllerFault
                                : fault.Reason,
                            Note = fault?.Detail
                        }
                    });
                    return;
                }

                default:
                    Logger.Warning($"[Bridge] Unsupported controller message kind '{envelope.Kind}'.");
                    return;
            }
        }

        private bool TryAccept(string commandId) {
            if (string.IsNullOrEmpty(commandId)) { return true; }
            lock (gate) {
                return processedCommandIds.Add(commandId);
            }
        }

        private void RememberGrant(string commandId, string grantedWindowId) {
            if (string.IsNullOrEmpty(commandId)) { return; }
            lock (gate) {
                if (grantedWindowByCommandId.Count > 64) { grantedWindowByCommandId.Clear(); }
                grantedWindowByCommandId[commandId] = grantedWindowId;
            }
        }

        private string TryGetCachedGrant(string commandId) {
            if (string.IsNullOrEmpty(commandId)) { return null; }
            lock (gate) {
                return grantedWindowByCommandId.TryGetValue(commandId, out var cached) ? cached : null;
            }
        }

        /// <summary>Any controller traffic proves the controller is still alive.</summary>
        private void RefreshSilenceDeadline(BridgeEnvelope envelope) {
            lock (gate) {
                lostAfterSilenceDeadline = null;
                if (windowId != null) {
                    windowSilenceDeadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.SilenceTimeoutMs);
                }
            }
        }

        private void Enqueue(BridgeRequest request) {
            lock (gate) {
                pending.Enqueue(request);
                if (request.Kind == BridgeRequestKind.Cancel
                    || request.Kind == BridgeRequestKind.ExternalLost
                    || request.Kind == BridgeRequestKind.SessionTimeout) {
                    terminalRequestPending = true;
                }
            }
            try { signal.Release(); } catch (SemaphoreFullException) { }
        }

        private void OnHeartbeat(object state) {
            if (disposed) { return; }
            Task.Run(async () => {
                try {
                    await PublishSessionStateCoreAsync(CancellationToken.None).ConfigureAwait(false);
                } catch (Exception ex) {
                    Logger.Error($"[Bridge] Heartbeat failed: {ex.Message}");
                }
            });
        }

        private Task PublishAsync(string kind, object payload, string replyTo, CancellationToken token) {
            if (broker == null) { return Task.CompletedTask; }
            var envelope = BridgeEnvelope.Create(
                kind: kind,
                sessionId: SessionId,
                commandId: Guid.NewGuid().ToString("N"),
                replyTo: replyTo,
                sequenceNumber: Interlocked.Increment(ref sequence),
                recipient: BridgeContract.ControllerRecipient,
                payload: payload);
            return broker.Publish(new BridgeEventMessage(envelope));
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            try { heartbeatTimer.Dispose(); } catch { }
            try { signal.Dispose(); } catch { }
        }
    }
}
