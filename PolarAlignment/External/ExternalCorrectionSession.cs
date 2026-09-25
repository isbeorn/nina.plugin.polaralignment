using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.External {

    /// <summary>
    /// Tunables of the external correction protocol. They are fixed in the plugin on purpose: TPPA
    /// publishes them in its <c>Capabilities</c> reply so the controller never has to hard code them,
    /// and the user needs no setting because the mode is detected automatically.
    /// </summary>
    public sealed class ExternalCorrectionOptions {
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
    public sealed class ExternalControllerRequest {
        public ExternalControllerRequestKind Kind { get; set; }
        public ExternalCorrectionEnvelope Envelope { get; set; }
        public string WindowId { get; set; }
        public string MeasurementId { get; set; }

        /// <summary>True when the same command was already served and must not be executed twice.</summary>
        public bool Duplicate { get; set; }

        public ExternalAdjustmentRequestPayload Adjustment { get; set; }
        public ExternalMeasurementRequestPayload MeasurementRequest { get; set; }
        public ExternalCompletionRequestPayload CompletionRequest { get; set; }
        public ExternalCancelPayload Cancel { get; set; }
        public ExternalFaultPayload Fault { get; set; }
    }

    /// <summary>
    /// One external correction session: owns the capture window, the silence watchdog, the
    /// heartbeat and the message bookkeeping. It is deliberately free of camera/mount access so the
    /// alignment loop stays in <see cref="Instructions.PolarAlignment"/> and this class stays testable.
    /// </summary>
    public sealed class ExternalCorrectionSession : IDisposable {
        private readonly IMessageBroker broker;
        private readonly object gate = new object();
        private readonly Queue<ExternalControllerRequest> pending = new Queue<ExternalControllerRequest>();
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

        private string state = ExternalCorrectionState.Preparing;
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

        public ExternalCorrectionSession(IMessageBroker broker, string sessionId = null) {
            this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
            Options = new ExternalCorrectionOptions();
            startedAt = DateTimeOffset.UtcNow;
            heartbeatTimer = new Timer(OnHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
        }

        public string SessionId { get; }

        public ExternalCorrectionOptions Options { get; private set; }

        /// <summary>
        /// Applies explicit tunables instead of reading them from the plugin settings. The alignment
        /// loop and the tests use this so both sides agree on the values published in the protocol.
        /// </summary>
        public void ApplyOptions(ExternalCorrectionOptions options) {
            if (options == null) { return; }
            Options = options;
            explicitOptions = true;
        }

        public bool IsActive => !disposed && state != ExternalCorrectionState.Ended;

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
                Options = new ExternalCorrectionOptions();
            }
            Options.ToleranceArcMin = toleranceArcMin;
            Options.ContinuousEstimation = continuousEstimation;

            state = ExternalCorrectionState.Preparing;
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
            if (state == ExternalCorrectionState.Preparing) {
                await PublishSessionStateAsync(state, null, token).ConfigureAwait(false);
            }

            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.ReadyTimeoutMs);
            while (true) {
                token.ThrowIfCancellationRequested();
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) {
                    Logger.Warning("[ExternalCorrection] No controller ready message received within the ready timeout.");
                    return false;
                }

                var request = await WaitForControllerRequestAsync(token, remaining).ConfigureAwait(false);
                if (request == null) { continue; }

                if (request.Kind == ExternalControllerRequestKind.ControllerReady) { return true; }
                if (request.Kind == ExternalControllerRequestKind.Cancel) { return false; }
                if (request.Kind == ExternalControllerRequestKind.SessionTimeout
                    || request.Kind == ExternalControllerRequestKind.ExternalLost) { return false; }

                Logger.Debug($"[ExternalCorrection] Ignoring '{request.Kind}' while waiting for the controller to become ready.");
            }
        }

        /// <summary>
        /// Waits for the next controller request. Returns TPPA-side events (window expired, session
        /// lost, session timeout) when a deadline is hit instead of a message.
        /// </summary>
        public Task<ExternalControllerRequest> WaitForControllerRequestAsync(CancellationToken token) {
            return WaitForControllerRequestAsync(token, null);
        }

        private async Task<ExternalControllerRequest> WaitForControllerRequestAsync(CancellationToken token, TimeSpan? maxWait) {
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
                        Logger.Warning("[ExternalCorrection] Controller stayed silent after the capture window expired.");
                        return new ExternalControllerRequest { Kind = ExternalControllerRequestKind.ExternalLost };
                    }
                    wait = Min(wait, lostAfterSilenceDeadline.Value - now);
                }

                var sessionRemaining = TimeSpan.FromSeconds(Options.SessionTimeoutSec) - RealRunningElapsed;
                if (sessionRemaining <= TimeSpan.Zero) {
                    Logger.Warning("[ExternalCorrection] Session reached the safety time limit.");
                    return new ExternalControllerRequest { Kind = ExternalControllerRequestKind.SessionTimeout };
                }
                wait = Min(wait, sessionRemaining);

                if (windowSilenceDeadline.HasValue) {
                    if (now >= windowSilenceDeadline.Value) {
                        CloseWindow(ExternalCorrectionReason.SilenceTimeout);
                        await PublishSessionStateAsync(state, ExternalCorrectionReason.SilenceTimeout, token).ConfigureAwait(false);
                        lostAfterSilenceDeadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.GraceAfterSilenceMs);
                        return new ExternalControllerRequest { Kind = ExternalControllerRequestKind.WindowExpired };
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
        public async Task PublishMeasurementAsync(ExternalMeasurementPayload payload, CancellationToken token) {
            if (payload == null) { return; }

            lock (gate) {
                samplesTaken++;
                lastMeasurementId = payload.MeasurementId;
                lastMeasurementWindowId = payload.WindowId;
                lastMeasurementBelowTolerance = payload.ToleranceReached;
                consecutiveBelowTolerance = payload.ConsecutiveBelowTolerance;
                state = ExternalCorrectionState.WaitingForRequest;
                stateReason = null;
            }

            payload.SessionId = SessionId;
            payload.SampleIndex = samplesTaken;
            payload.ToleranceArcMin = Options.ToleranceArcMin;
            payload.ContinuousEstimation = Options.ContinuousEstimation;

            await PublishAsync(ExternalCorrectionKind.Measurement, payload, null, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Grants a capture window for the adjustment announced by the controller and answers with
        /// <c>AdjustmentGranted</c>. Returns the granted window id.
        /// </summary>
        public async Task<string> GrantWindowAsync(ExternalControllerRequest request, CancellationToken token) {
            if (request == null) { return null; }

            var cached = TryGetCachedGrant(request.Envelope?.CommandId);
            if (cached != null) {
                await PublishAdjustmentGrantedAsync(cached, request.Adjustment?.MeasurementId, token).ConfigureAwait(false);
                return cached;
            }

            var sessionRemaining = TimeSpan.FromSeconds(Options.SessionTimeoutSec) - RealRunningElapsed;
            var grant = new ExternalAdjustmentGrantPayload {
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
                state = ExternalCorrectionState.WindowOpen;
                stateReason = null;
                RememberGrant(request.Envelope?.CommandId, grant.WindowId);
            }

            await PublishAdjustmentGrantedAsync(grant.WindowId, grant.MeasurementId, token).ConfigureAwait(false);
            return grant.WindowId;
        }

        private async Task PublishAdjustmentGrantedAsync(string grantedWindowId, string measurementId, CancellationToken token) {
            var payload = new ExternalAdjustmentGrantPayload {
                WindowId = grantedWindowId,
                MeasurementId = measurementId,
                MaxWindowMs = (long)Math.Max(0, (TimeSpan.FromSeconds(Options.SessionTimeoutSec) - RealRunningElapsed).TotalMilliseconds),
                SilenceTimeoutMs = Options.SilenceTimeoutMs,
                GrantedAtUtc = DateTimeOffset.UtcNow
            };
            await PublishAsync(ExternalCorrectionKind.AdjustmentGranted, payload, null, token).ConfigureAwait(false);
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
                state = ExternalCorrectionState.Measuring;
                stateReason = reason;
            }
        }

        /// <summary>Marks the loop as being in the verification phase requested by the controller.</summary>
        public void EnterVerifying() {
            lock (gate) {
                state = ExternalCorrectionState.Verifying;
                stateReason = ExternalCorrectionReason.CompletionRequested;
            }
        }

        /// <summary>Marks the loop as capturing the three reference points.</summary>
        public void EnterMeasuring(string reason) {
            lock (gate) {
                state = ExternalCorrectionState.Measuring;
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
            var payload = new ExternalPauseRequestPayload {
                Paused = paused,
                Reason = reason
            };
            return PublishAsync(ExternalCorrectionKind.PauseRequested, payload, null, token);
        }

        private Task PublishSessionStateCoreAsync(CancellationToken token) {
            ExternalSessionStatePayload payload;
            lock (gate) {
                payload = new ExternalSessionStatePayload {
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
            return PublishAsync(ExternalCorrectionKind.SessionState, payload, null, token);
        }

        /// <summary>Asks the controller to stop and waits for its acknowledgement within the ack timeout.</summary>
        public async Task<string> RequestStopAsync(string reason, CancellationToken token) {
            lock (gate) {
                if (stopRequested) { return hardwareStopStatus; }
                stopRequested = true;
                stopAcknowledged = false;
            }

            var payload = new ExternalStopRequestPayload {
                Reason = reason,
                AckTimeoutMs = Options.StopAckTimeoutMs
            };
            await PublishAsync(ExternalCorrectionKind.StopRequested, payload, null, token).ConfigureAwait(false);

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

            Logger.Warning($"[ExternalCorrection] Controller did not acknowledge the stop request within {Options.StopAckTimeoutMs} ms.");
            return ExternalHardwareStopStatus.Unknown;
        }

        /// <summary>Publishes the final message of the session.</summary>
        public async Task EndAsync(string reason, bool achieved, ExternalSessionEndedPayload detail, CancellationToken token) {
            TimeSpan elapsed;
            lock (gate) {
                CloseWindow(reason);
                state = ExternalCorrectionState.Ended;
                stateReason = reason;
                heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
                elapsed = RealRunningElapsed;
            }

            var payload = detail ?? new ExternalSessionEndedPayload();
            payload.Reason = reason;
            payload.Achieved = achieved;
            payload.ToleranceUsedArcMin = Options.ToleranceArcMin;
            if (payload.SamplesUsed == 0) { payload.SamplesUsed = samplesTaken; }
            if (string.IsNullOrEmpty(payload.HardwareStopStatus)) {
                payload.HardwareStopStatus = hardwareStopStatus ?? ExternalHardwareStopStatus.Unknown;
            }

            Logger.Info($"[ExternalCorrection] Session {SessionId} ended. Reason: {reason}; achieved: {achieved}; samples: {payload.SamplesUsed}; elapsed: {elapsed:hh\\:mm\\:ss}.");
            await PublishAsync(ExternalCorrectionKind.SessionEnded, payload, null, token).ConfigureAwait(false);
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
        public void HandleEnvelope(ExternalCorrectionEnvelope envelope) {
            if (envelope == null || disposed) { return; }

            if (envelope.Version != ExternalCorrectionContract.InterfaceVersion) {
                Logger.Warning($"[ExternalCorrection] Ignoring controller message with interface version {envelope.Version}.");
                return;
            }

            if (!string.IsNullOrEmpty(envelope.IntendedRecipient)
                && !string.Equals(envelope.IntendedRecipient, ExternalCorrectionContract.TppaRecipient, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            if (!string.IsNullOrEmpty(envelope.SessionId)
                && !string.Equals(envelope.SessionId, SessionId, StringComparison.Ordinal)) {
                Logger.Warning($"[ExternalCorrection] Ignoring controller message for session {envelope.SessionId}.");
                return;
            }

            switch (envelope.Kind) {
                case ExternalCorrectionKind.KeepAlive:
                    RefreshSilenceDeadline(envelope);
                    return;

                case ExternalCorrectionKind.Stopped:
                    lock (gate) {
                        stopAcknowledged = true;
                        var stopped = envelope.PayloadAs<ExternalStoppedPayload>();
                        hardwareStopStatus = string.IsNullOrEmpty(stopped?.HardwareStopStatus)
                            ? ExternalHardwareStopStatus.Ok
                            : stopped.HardwareStopStatus;
                    }
                    Enqueue(new ExternalControllerRequest {
                        Kind = ExternalControllerRequestKind.StopAcknowledged,
                        Envelope = envelope
                    });
                    return;

                case ExternalCorrectionKind.ControllerReady:
                    if (!TryAccept(envelope.CommandId)) { return; }
                    Enqueue(new ExternalControllerRequest {
                        Kind = ExternalControllerRequestKind.ControllerReady,
                        Envelope = envelope
                    });
                    return;

                case ExternalCorrectionKind.BeginAdjustment: {
                    var adjustment = envelope.PayloadAs<ExternalAdjustmentRequestPayload>();
                    if (!TryAccept(envelope.CommandId)) {
                        // Idempotent repeat: answer with the window that this command already got.
                        var cachedWindow = TryGetCachedGrant(envelope.CommandId);
                        if (cachedWindow != null) {
                            _ = PublishAdjustmentGrantedAsync(cachedWindow, adjustment?.MeasurementId, CancellationToken.None);
                        }
                        return;
                    }
                    Enqueue(new ExternalControllerRequest {
                        Kind = ExternalControllerRequestKind.AdjustWindow,
                        Envelope = envelope,
                        MeasurementId = adjustment?.MeasurementId,
                        Adjustment = adjustment
                    });
                    return;
                }

                case ExternalCorrectionKind.RequestMeasurement: {
                    var payload = envelope.PayloadAs<ExternalMeasurementRequestPayload>();
                    var duplicate = !TryAccept(envelope.CommandId);
                    if (!duplicate) {
                        // "Close the window and capture now" is a protocol rule, so the session applies
                        // it as soon as the request arrives instead of leaving it to the caller loop.
                        CloseWindow(payload?.Reason ?? ExternalCorrectionReason.StepFinished);
                        lock (gate) {
                            servedMeasurementRequests.Add(envelope.CommandId);
                        }
                    }
                    Enqueue(new ExternalControllerRequest {
                        Kind = ExternalControllerRequestKind.RequestMeasurement,
                        Envelope = envelope,
                        Duplicate = duplicate,
                        WindowId = payload?.WindowId,
                        MeasurementRequest = payload
                    });
                    return;
                }

                case ExternalCorrectionKind.RequestCompletion: {
                    var completion = envelope.PayloadAs<ExternalCompletionRequestPayload>();
                    if (!TryAccept(envelope.CommandId)) { return; }
                    // A completion request while a window is open closes it and verifies instead of
                    // being rejected, so the controller can always stop the correction chain.
                    CloseWindow(ExternalCorrectionReason.CompletionRequested);
                    Enqueue(new ExternalControllerRequest {
                        Kind = ExternalControllerRequestKind.RequestCompletion,
                        Envelope = envelope,
                        WindowId = completion?.WindowId,
                        CompletionRequest = completion
                    });
                    return;
                }

                case ExternalCorrectionKind.Cancel:
                    if (!TryAccept(envelope.CommandId)) { return; }
                    Enqueue(new ExternalControllerRequest {
                        Kind = ExternalControllerRequestKind.Cancel,
                        Envelope = envelope,
                        Cancel = envelope.PayloadAs<ExternalCancelPayload>()
                    });
                    return;

                case ExternalCorrectionKind.Fault: {
                    var fault = envelope.PayloadAs<ExternalFaultPayload>();
                    lock (gate) {
                        hardwareStopStatus = string.IsNullOrEmpty(fault?.HardwareStopStatus)
                            ? ExternalHardwareStopStatus.Unknown
                            : fault.HardwareStopStatus;
                    }
                    Enqueue(new ExternalControllerRequest {
                        Kind = ExternalControllerRequestKind.Cancel,
                        Envelope = envelope,
                        Fault = fault,
                        Cancel = new ExternalCancelPayload {
                            Reason = string.IsNullOrEmpty(fault?.Reason)
                                ? ExternalCorrectionReason.ControllerFault
                                : fault.Reason,
                            Note = fault?.Detail
                        }
                    });
                    return;
                }

                default:
                    Logger.Warning($"[ExternalCorrection] Unsupported controller message kind '{envelope.Kind}'.");
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
        private void RefreshSilenceDeadline(ExternalCorrectionEnvelope envelope) {
            lock (gate) {
                lostAfterSilenceDeadline = null;
                if (windowId != null) {
                    windowSilenceDeadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.SilenceTimeoutMs);
                }
            }
        }

        private void Enqueue(ExternalControllerRequest request) {
            lock (gate) {
                pending.Enqueue(request);
                if (request.Kind == ExternalControllerRequestKind.Cancel
                    || request.Kind == ExternalControllerRequestKind.ExternalLost
                    || request.Kind == ExternalControllerRequestKind.SessionTimeout) {
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
                    Logger.Error($"[ExternalCorrection] Heartbeat failed: {ex.Message}");
                }
            });
        }

        private Task PublishAsync(string kind, object payload, string replyTo, CancellationToken token) {
            if (broker == null) { return Task.CompletedTask; }
            var envelope = ExternalCorrectionEnvelope.Create(
                kind: kind,
                sessionId: SessionId,
                commandId: Guid.NewGuid().ToString("N"),
                replyTo: replyTo,
                sequenceNumber: Interlocked.Increment(ref sequence),
                recipient: ExternalCorrectionContract.ControllerRecipient,
                payload: payload);
            return broker.Publish(new ExternalCorrectionEventMessage(envelope));
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            try { heartbeatTimer.Dispose(); } catch { }
            try { signal.Dispose(); } catch { }
        }
    }
}
