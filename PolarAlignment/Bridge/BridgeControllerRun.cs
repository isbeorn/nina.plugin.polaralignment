using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Bridge {

    /// <summary>What the controller asked for after it was handed a measurement.</summary>
    public enum BridgeControllerAskKind {
        /// <summary>The controller finished its adjustment and wants the next measurement.</summary>
        AdjustmentFinished,

        /// <summary>The controller wants the result verified before it finishes.</summary>
        VerificationRequested
    }

    /// <summary>One answer of the controller, i.e. the single interaction TPPA is waiting for.</summary>
    public sealed class BridgeControllerAsk {
        public BridgeControllerAskKind Kind { get; set; }
        public string MeasurementId { get; set; }
        public string WindowId { get; set; }
        public string Reason { get; set; }
        public double SettleSeconds { get; set; }
        public bool DetectingDirection { get; set; }
    }

    /// <summary>How the controller ended the run, ready for the operator: the alignment loop turns this into a toast.</summary>
    public sealed class BridgeRunFailedEventArgs : EventArgs {
        public string Reason { get; set; }
        public string Note { get; set; }
        public bool IsFault { get; set; }
        public bool ControllerStoppedAnswering { get; set; }
    }

    /// <summary>
    /// The external correction step of one alignment run. It holds everything the broker needs for that step -
    /// who the controller is, what TPPA is waiting for, the readiness the controller last reported - and nothing
    /// else: capturing, solving and publishing the error stay in the alignment loop, which is why the loop drives
    /// this class through <see cref="PublishMeasurementAndWaitAsync"/> instead of the other way round.
    /// <para>
    /// There is exactly one pending interaction at a time and exactly one way for a run to end badly: a
    /// controller that cancels, reports a fault or stops answering ends the session, cancels the run token and
    /// throws, so the same failure path serves the reference frames, the adjustment and the verification.
    /// </para>
    /// </summary>
    public sealed class BridgeControllerRun : IDisposable {
        private readonly IMessageBroker broker;
        private readonly object gate = new object();
        private readonly CancellationTokenSource runTokens;
        private readonly Timer heartbeatTimer;

        private TaskCompletionSource<BridgeControllerAsk> pending =
            new TaskCompletionSource<BridgeControllerAsk>(TaskCreationOptions.RunContinuationsAsynchronously);

        private Guid? ownerId;
        private string controllerAddress = BridgeContract.ControllerRecipient;
        private bool disposed;
        private bool ended;

        private string state = BridgeState.Preparing;
        private string stateReason;
        private int samplesTaken;
        private string lastMeasurementId;

        /// <summary>
        /// True when the measurement that was published last was usable. An unusable one may not be answered with a
        /// move, because the correction would be planned from a measurement the run could not trust.
        /// </summary>
        private bool lastMeasurementUsable;
        private string windowId;
        private string windowMeasurementId;
        private DateTimeOffset windowOpenedAt;
        private DateTimeOffset? windowSilenceDeadline;
        private DateTimeOffset? lostAfterSilenceDeadline;

        /// <summary>
        /// Time the controller has to answer the measurement that was just published. A wait that the controller
        /// holds nothing for needs a deadline of its own: a controller that keeps chatting (readiness, keep-alives)
        /// without answering would otherwise keep the run waiting for ever, because those messages refresh the
        /// silence watchdog of the capture window but are not an answer.
        /// </summary>
        private DateTimeOffset? answerDeadline;

        /// <summary>The single interaction this run is waiting for.</summary>
        private enum PendingInteraction {
            None,

            /// <summary>A measurement was published and the controller has to answer it.</summary>
            Adjustment,

            /// <summary>
            /// The controller holds a capture window for the measurement it was handed and has to report the move as
            /// finished or ask for completion.
            /// </summary>
            Completion
        }

        /// <summary>
        /// What this run is waiting for, so a controller message that belongs to another moment - a move for a
        /// measurement that is over, a window that was already closed - is refused instead of completing an
        /// interaction the loop never opened.
        /// </summary>
        private PendingInteraction pendingInteraction = PendingInteraction.None;
        private bool stopRequested;
        private bool stopAcknowledged;
        private string hardwareStopStatus;
        private bool controllerReadinessSeen;
        private bool controllerHardwareReady;
        private bool? controllerHardwareConnected;
        private string controllerReadyNote;
        private bool controllerFaultSeen;
        private string controllerFaultReason;
        private string controllerFaultDetail;
        private Exception failure;
        private bool explicitOptions;

        private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        public BridgeControllerRun(IMessageBroker broker, CancellationTokenSource runTokens, string sessionId = null) {
            this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
            this.runTokens = runTokens;
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
            Options = new BridgeOptions();
            heartbeatTimer = new Timer(OnHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
        }

        public string SessionId { get; }

        public BridgeOptions Options { get; private set; }

        public bool IsActive => !disposed && !ended;

        public string State => state;

        /// <summary>Broker identity of the controller this run belongs to, null while it does not identify itself.</summary>
        public Guid? OwnerId { get { lock (gate) { return ownerId; } } }

        /// <summary>Address TPPA puts in <c>IntendedRecipient</c> of every message this run publishes.</summary>
        public string ControllerAddress { get { lock (gate) { return controllerAddress; } } }

        /// <summary>Reason of the last controller fault, kept so the run can report what the controller said.</summary>
        public string ControllerFaultReason { get { lock (gate) { return controllerFaultReason; } } }

        public string ControllerFaultDetail { get { lock (gate) { return controllerFaultDetail; } } }

        public bool HasControllerFault { get { lock (gate) { return controllerFaultSeen; } } }

        /// <summary>
        /// Latest hardware readiness the controller reported. TPPA re-reads it at the points where it decides
        /// (hand-over, resume, before a capture): the controller republishes it whenever its axes go busy or
        /// idle, so no queue of readiness messages is needed.
        /// </summary>
        public bool ControllerHardwareReady { get { lock (gate) { return controllerReadinessSeen && controllerHardwareReady; } } }

        /// <summary>
        /// Whether the controller reported its link to the alignment hardware. A controller that never reported
        /// readiness counts as not connected, so the run waits for the answer instead of skipping that wait.
        /// </summary>
        public bool ControllerHardwareConnected { get { lock (gate) { return controllerReadinessSeen && (controllerHardwareConnected ?? controllerHardwareReady); } } }

        public string ControllerReadyNote { get { lock (gate) { return controllerReadyNote; } } }

        public string CurrentWindowId { get { lock (gate) { return windowId; } } }

        public void ApplyOptions(BridgeOptions options) {
            if (options == null) { return; }
            Options = options;
            explicitOptions = true;
        }

        /// <summary>
        /// Binds the controller the operator selected to this run, once. From then on the owner never changes,
        /// and messages from anybody else belong to another controller's run.
        /// </summary>
        public void BindOwner(Guid? controllerId, string address) {
            lock (gate) {
                ownerId = controllerId == Guid.Empty ? null : controllerId;
                controllerAddress = string.IsNullOrWhiteSpace(address) ? BridgeContract.ControllerRecipient : address;
            }
        }

        /// <summary>
        /// True when the message belongs to the controller this run was bound to. A message that identifies no
        /// sender is never filtered, so a controller of an older build keeps working.
        /// </summary>
        public bool AcceptsSender(Guid senderId) {
            lock (gate) {
                return !ownerId.HasValue || senderId == Guid.Empty || senderId == ownerId.Value;
            }
        }

        /// <summary>Opens the run: publishes the preparing state and starts the heartbeat.</summary>
        public async Task OpenAsync(double toleranceArcMin, bool continuousEstimation, CancellationToken token) {
            if (!explicitOptions) { Options = new BridgeOptions(); }
            Options.ToleranceArcMin = toleranceArcMin;
            Options.ContinuousEstimation = continuousEstimation;

            lock (gate) {
                state = BridgeState.Preparing;
                stateReason = null;
            }

            heartbeatTimer.Change(Options.HeartbeatMs, Options.HeartbeatMs);

            // The controller adopts the session id and resets its state from this message, so "Preparing" has to go out
            // before anything else: without it a controller keeps the id of its previous session, and this run then
            // ignores every readiness (and every other message) it sends as "another session". The sweep state follows
            // from the alignment loop.
            await PublishSessionStateAsync(BridgeState.Preparing, null, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Publishes a measurement and waits for the controller's single answer: it either finished its
        /// adjustment (the loop captures again) or wants the result verified. A cancel or a fault from the
        /// controller, or a controller that stops answering, ends the run through the one failure path.
        /// </summary>
        public async Task<BridgeControllerAsk> PublishMeasurementAndWaitAsync(BridgeMeasurementPayload payload, CancellationToken token) {
            // Every interaction gets a waiter of its own. A finished one must never be awaited again: the answer of
            // the previous measurement would then be handed out as the answer of this one, which makes the loop
            // capture over and over and never see the controller ask for completion.
            lock (gate) {
                pending = new TaskCompletionSource<BridgeControllerAsk>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await PublishMeasurementAsync(payload, token).ConfigureAwait(false);

            var ask = await WaitForAskAsync(token).ConfigureAwait(false);
            if (ask == null) {
                throw failure ?? new OperationCanceledException("The external correction run ended.");
            }
            return ask;
        }

        /// <summary>
        /// Asks the controller to stop the alignment axes and waits for its acknowledgement, which is what the
        /// operator needs after pressing STOP on this side.
        /// </summary>
        public async Task<string> RequestStopAsync(string reason, CancellationToken token) {
            lock (gate) {
                if (stopRequested) { return hardwareStopStatus; }
                stopRequested = true;
                stopAcknowledged = false;
            }

            await PublishAsync(BridgeKind.StopRequested,
                               new BridgeStopRequestPayload { Reason = reason, AckTimeoutMs = Options.StopAckTimeoutMs },
                               token).ConfigureAwait(false);

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

        /// <summary>Publishes the final message of the run.</summary>
        public async Task EndAsync(string reason, bool achieved, BridgeSessionEndedPayload detail, CancellationToken token) {
            BridgeSessionEndedPayload endPayload;
            lock (gate) {
                ended = true;
                CloseWindowLocked(reason);
                state = BridgeState.Ended;
                stateReason = reason;
                endPayload = detail ?? new BridgeSessionEndedPayload();
                endPayload.Reason = reason;
                endPayload.Achieved = achieved;
                endPayload.ToleranceUsedArcMin = Options.ToleranceArcMin;
                if (endPayload.SamplesUsed == 0) { endPayload.SamplesUsed = samplesTaken; }
                if (string.IsNullOrEmpty(endPayload.HardwareStopStatus)) {
                    endPayload.HardwareStopStatus = hardwareStopStatus ?? BridgeHardwareStopStatus.Unknown;
                }
            }

            heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);

            Logger.Info($"[Bridge] External correction run {SessionId} ended. Reason: {reason}; achieved: {achieved}; samples: {endPayload.SamplesUsed}.");
            await PublishAsync(BridgeKind.SessionEnded, endPayload, token).ConfigureAwait(false);
        }

        /// <summary>Publishes the session state, e.g. after the reference sweep.</summary>
        public Task PublishSessionStateAsync(string newState, string reason, CancellationToken token) {
            BridgeSessionStatePayload payload;
            lock (gate) {
                if (newState != null) { state = newState; }
                if (reason != null) { stateReason = reason; }
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

            return PublishAsync(BridgeKind.SessionState, payload, token);
        }

        /// <summary>
        /// Tells the controller that the operator paused or resumed the run, so it stops a move in progress.
        /// Fire and forget: a pause must not depend on the broker.
        /// </summary>
        public Task PublishPauseRequestAsync(bool paused, string reason, CancellationToken token) {
            return PublishAsync(BridgeKind.PauseRequested, new BridgePauseRequestPayload { Paused = paused, Reason = reason }, token);
        }

        /// <summary>
        /// The settle the controller wants before the first capture after a pause, and 0 while no pause is waiting to be
        /// resumed. Its axes were stopped for the pause, so that moment belongs to the resume, not to the pause.
        /// </summary>
        private double resumeSettleSeconds;

        /// <summary>Reads and clears the settle the controller asked for when it answered the pause.</summary>
        public double TakeResumeSettleSeconds() {
            lock (gate) {
                var settle = resumeSettleSeconds;
                resumeSettleSeconds = 0;
                return settle;
            }
        }

        /// <summary>
        /// Ends the interaction that is waiting for the controller's answer because the operator paused the run. The
        /// controller holds that answer until the resume, so waiting for it would keep the run inside the external step
        /// while the operator has nothing left to resume: the loop is released here and stands at its own pause check
        /// instead. Nothing is sent to the controller - it was told about the pause already - and the capture window is
        /// closed, because no move is going to happen inside it.
        /// </summary>
        public void ReleaseInteractionForPause() {
            BridgeControllerAsk ask;
            lock (gate) {
                if (pendingInteraction == PendingInteraction.None) { return; }

                // The measurement was not corrected and the loop measures again after the resume, so the ask carries
                // neither a window nor a settle time.
                ask = new BridgeControllerAsk {
                    Kind = BridgeControllerAskKind.AdjustmentFinished,
                    MeasurementId = lastMeasurementId,
                    Reason = BridgeReason.Paused
                };
            }

            Logger.Info("[Bridge] Paused while the controller was answering: the interaction is ended so the run can stand at the pause.");
            CloseWindow(BridgeReason.Paused);
            CompletePending(ask);
        }

        /// <summary>
        /// Waits until the controller reports its hardware as ready again, which is what the loop does after the
        /// operator resumed a paused run: the controller stopped its axes for the pause, so the next measurement must
        /// not be handed over while it is still busy. Returns false when readiness did not come back in time - the run
        /// then continues anyway, because the answer deadline of the measurement takes over from here.
        /// </summary>
        public async Task<bool> WaitForHardwareReadyAsync(TimeSpan timeout, CancellationToken token) {
            if (ControllerHardwareReady) { return true; }

            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline) {
                if (ControllerHardwareReady) { return true; }
                if (ended || HasControllerFault) { return false; }
                await Task.Delay(250, token).ConfigureAwait(false);
            }

            return ControllerHardwareReady;
        }

        /// <summary>
        /// Takes one inbound controller message. Readiness and keep-alive only refresh what the loop reads at its
        /// decision points; the messages that answer the pending interaction complete it, and a cancel or a fault
        /// runs the one failure path.
        /// </summary>
        public void HandleEnvelope(BridgeEnvelope envelope) {
            if (envelope == null || disposed || ended) { return; }

            if (envelope.Version != BridgeContract.InterfaceVersion) {
                Logger.Warning($"[Bridge] Ignoring controller message with interface version {envelope.Version}.");
                return;
            }

            if (!string.IsNullOrEmpty(envelope.IntendedRecipient)
                && !string.Equals(envelope.IntendedRecipient, BridgeContract.TppaRecipient, StringComparison.OrdinalIgnoreCase)) {
                Logger.Warning($"[Bridge] Ignoring a '{envelope.Kind}' message addressed to \"{envelope.IntendedRecipient}\": a controller has to address TPPA as \"{BridgeContract.TppaRecipient}\", or leave IntendedRecipient empty.");
                return;
            }

            if (!string.IsNullOrEmpty(envelope.SessionId)
                && !string.Equals(envelope.SessionId, SessionId, StringComparison.Ordinal)) {
                Logger.Warning($"[Bridge] Ignoring controller message for session {envelope.SessionId}.");
                return;
            }

            switch (envelope.Kind) {
                case BridgeKind.KeepAlive:
                    RefreshSilence();
                    return;

                case BridgeKind.ControllerReady: {
                    var readiness = envelope.PayloadAs<BridgeControllerReadyPayload>();
                    lock (gate) {
                        controllerReadinessSeen = true;
                        controllerHardwareReady = readiness?.HardwareReady ?? true;
                        controllerHardwareConnected = readiness?.HardwareConnected;
                        controllerReadyNote = readiness?.Note;
                    }
                    return;
                }

                case BridgeKind.Stopped: {
                    var stopped = envelope.PayloadAs<BridgeStoppedPayload>();
                    lock (gate) {
                        stopAcknowledged = true;
                        hardwareStopStatus = string.IsNullOrEmpty(stopped?.HardwareStopStatus)
                            ? BridgeHardwareStopStatus.Ok
                            : stopped.HardwareStopStatus;
                    }
                    return;
                }

                case BridgeKind.BeginAdjustment: {
                    // The controller announces a move on the measurement it was handed. TPPA answers with the
                    // window it may hold while moving, but only when the announcement belongs to the measurement
                    // this run is waiting on; the interaction itself stays pending until the move is reported
                    // finished.
                    var adjustment = envelope.PayloadAs<BridgeAdjustmentRequestPayload>();
                    _ = HandleAdjustmentAnnouncementAsync(adjustment).ContinueWith(task => {
                        if (task.Exception != null) {
                            Logger.Error($"[Bridge] Failed to grant a capture window: {task.Exception.GetBaseException().Message}");
                        }
                    }, TaskScheduler.Default);
                    return;
                }

                case BridgeKind.RequestMeasurement: {
                    var request = envelope.PayloadAs<BridgeMeasurementRequestPayload>();
                    if (!AcceptsAnswer(request?.WindowId, BridgeKind.RequestMeasurement)) { return; }
                    CloseWindow(request?.Reason ?? BridgeReason.StepFinished);

                    // An answer that comes with the pause carries the settle the controller wants before the capture
                    // that follows the resume: its axes were stopped, so the mechanics need that moment then.
                    if (string.Equals(request?.Reason, BridgeReason.Paused, StringComparison.Ordinal)
                        && request?.SettleSeconds > 0) {
                        lock (gate) { resumeSettleSeconds = request.SettleSeconds.Value; }
                    }

                    CompletePending(new BridgeControllerAsk {
                        Kind = BridgeControllerAskKind.AdjustmentFinished,
                        MeasurementId = lastMeasurementId,
                        WindowId = request?.WindowId ?? windowId,
                        Reason = request?.Reason,
                        SettleSeconds = request?.SettleSeconds ?? 0,
                        DetectingDirection = false
                    });
                    return;
                }

                case BridgeKind.RequestCompletion: {
                    var completion = envelope.PayloadAs<BridgeCompletionRequestPayload>();
                    if (!AcceptsAnswer(completion?.WindowId, BridgeKind.RequestCompletion)) { return; }
                    CloseWindow(BridgeReason.CompletionRequested);
                    CompletePending(new BridgeControllerAsk {
                        Kind = BridgeControllerAskKind.VerificationRequested,
                        MeasurementId = lastMeasurementId,
                        WindowId = completion?.WindowId,
                        Reason = BridgeReason.CompletionRequested
                    });
                    return;
                }

                case BridgeKind.Cancel: {
                    var cancel = envelope.PayloadAs<BridgeCancelPayload>();
                    Fail(envelope.Kind, cancel?.Reason ?? BridgeReason.ControllerCancel, cancel?.Note, isFault: false);
                    return;
                }

                case BridgeKind.Fault: {
                    var fault = envelope.PayloadAs<BridgeFaultPayload>();
                    lock (gate) {
                        controllerFaultSeen = true;
                        controllerFaultReason = fault?.Reason;
                        controllerFaultDetail = fault?.Detail;
                        hardwareStopStatus = string.IsNullOrEmpty(fault?.HardwareStopStatus)
                            ? BridgeHardwareStopStatus.Unknown
                            : fault.HardwareStopStatus;
                    }
                    Fail(envelope.Kind,
                         string.IsNullOrEmpty(fault?.Reason) ? BridgeReason.ControllerFault : fault.Reason,
                         fault?.Detail,
                         isFault: true);
                    return;
                }

                default:
                    Logger.Warning($"[Bridge] Unsupported controller message kind '{envelope.Kind}'.");
                    return;
            }
        }

        /// <summary>
        /// Takes the announcement of a move. It is only accepted for the measurement this run handed over and only
        /// while that interaction is pending - either the announcement itself is the answer, or the window of that
        /// same measurement is still open and the announcement is a repeat, which is answered with the same grant.
        /// Everything else is refused with a log line instead of opening a window the loop never asked for.
        /// </summary>
        private async Task HandleAdjustmentAnnouncementAsync(BridgeAdjustmentRequestPayload adjustment) {
            var measurementId = adjustment?.MeasurementId;

            string currentMeasurementId;
            string openWindowId;
            PendingInteraction pending;
            bool endedNow;
            bool measurementUsable;
            lock (gate) {
                currentMeasurementId = lastMeasurementId;
                openWindowId = windowId;
                pending = pendingInteraction;
                endedNow = ended;
                measurementUsable = lastMeasurementUsable;
            }

            // A controller of an older build sends no measurement id, so an empty one is not treated as a mismatch.
            var forCurrentMeasurement = string.IsNullOrEmpty(measurementId)
                                        || string.Equals(measurementId, currentMeasurementId, StringComparison.Ordinal);

            if (endedNow || !forCurrentMeasurement || !measurementUsable || (openWindowId == null && pending != PendingInteraction.Adjustment)) {
                Logger.Warning($"[Bridge] Refused a '{BridgeKind.BeginAdjustment}' for measurement {measurementId ?? "(none)"}: this run is waiting for measurement " +
                               $"{currentMeasurementId ?? "(none)"}, that measurement {(!measurementUsable ? "is not usable" : "is usable")} and " +
                               (openWindowId == null ? "no capture window is open." : $"window {openWindowId} is open."));
                return;
            }

            await GrantWindowAsync(adjustment, openWindowId).ConfigureAwait(false);
        }

        /// <summary>
        /// True when an answer may complete the pending interaction. A completion that names a window other than the
        /// one this run holds is stale - it belongs to a window that is already closed - and an answer that arrives
        /// while nothing is pending is ignored, because the loop is not waiting for it.
        /// </summary>
        private bool AcceptsAnswer(string windowIdInAnswer, string kind) {
            string openWindowId;
            PendingInteraction pending;
            bool endedNow;
            lock (gate) {
                openWindowId = windowId;
                pending = pendingInteraction;
                endedNow = ended;
            }

            if (endedNow) { return false; }

            if (!string.IsNullOrEmpty(windowIdInAnswer) && openWindowId != null
                && !string.Equals(windowIdInAnswer, openWindowId, StringComparison.Ordinal)) {
                Logger.Warning($"[Bridge] Refused a '{kind}' for window {windowIdInAnswer}: this run holds window {openWindowId}.");
                return false;
            }

            if (pending == PendingInteraction.None) {
                Logger.Info($"[Bridge] Ignored a '{kind}': no measurement is pending in this run, so there is nothing to answer.");
                return false;
            }

            return true;
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            try { heartbeatTimer.Dispose(); } catch { }
            CompletePending(null);
        }

        /// <summary>Wall time the run has been going, excluding the time capture windows were open.</summary>
        private TimeSpan RealRunningElapsed {
            get {
                var now = DateTimeOffset.UtcNow;
                var windowTime = TimeSpan.Zero;
                if (windowOpenedAt != default) {
                    windowTime += now - windowOpenedAt;
                }
                var total = now - startedAt - windowTime;
                return total < TimeSpan.Zero ? TimeSpan.Zero : total;
            }
        }

        /// <summary>
        /// Waits for the controller's answer. TPPA-side deadlines are part of the same wait: a measurement that is
        /// never answered ends the run after the silence timeout, a capture window that goes silent ends it after
        /// the grace period, and the safety limit of the session ends it too.
        /// </summary>
        private async Task<BridgeControllerAsk> WaitForAskAsync(CancellationToken token) {
            while (true) {
                token.ThrowIfCancellationRequested();

                var wait = NextDeadline(out var expiredReason, out var windowWentSilent);
                if (windowWentSilent) {
                    await PublishSessionStateAsync(null, BridgeReason.SilenceTimeout, CancellationToken.None).ConfigureAwait(false);
                }

                if (expiredReason != null) {
                    FailLost(expiredReason);
                    return await pending.Task.ConfigureAwait(false);
                }

                var completed = await Task.WhenAny(pending.Task, Task.Delay(wait, token)).ConfigureAwait(false);
                if (completed == pending.Task) { return await pending.Task.ConfigureAwait(false); }
            }
        }

        /// <summary>
        /// Time left until the next TPPA-side deadline of the wait. A deadline that already passed is reported
        /// through <paramref name="expiredReason"/> instead of a negative wait.
        /// </summary>
        private TimeSpan NextDeadline(out string expiredReason, out bool windowWentSilent) {
            expiredReason = null;
            windowWentSilent = false;

            lock (gate) {
                var now = DateTimeOffset.UtcNow;
                TimeSpan wait;

                if (lostAfterSilenceDeadline.HasValue) {
                    if (now >= lostAfterSilenceDeadline.Value) {
                        expiredReason = BridgeReason.ExternalLost;
                        return TimeSpan.Zero;
                    }
                    wait = lostAfterSilenceDeadline.Value - now;
                } else if (windowSilenceDeadline.HasValue) {
                    if (now >= windowSilenceDeadline.Value) {
                        // The window went silent: the controller is told, then gets the grace period before the run
                        // is ended as lost.
                        CloseWindowLocked(BridgeReason.SilenceTimeout);
                        lostAfterSilenceDeadline = now.AddMilliseconds(Options.GraceAfterSilenceMs);
                        windowWentSilent = true;
                        wait = lostAfterSilenceDeadline.Value - now;
                    } else {
                        wait = windowSilenceDeadline.Value - now;
                    }
                } else if (answerDeadline.HasValue) {
                    if (now >= answerDeadline.Value) {
                        // The controller was handed a measurement and never answered it. Readiness and keep-alive
                        // messages are not an answer, and silence does not prove that its axes stopped, so the run
                        // ends here instead of waiting for an answer that may never come.
                        expiredReason = BridgeReason.SilenceTimeout;
                        return TimeSpan.Zero;
                    }
                    wait = answerDeadline.Value - now;
                } else {
                    wait = TimeSpan.FromSeconds(5);
                }

                var sessionRemaining = TimeSpan.FromSeconds(Options.SessionTimeoutSec) - RealRunningElapsed;
                if (sessionRemaining <= TimeSpan.Zero) {
                    expiredReason = BridgeReason.SessionTimeout;
                    return TimeSpan.Zero;
                }

                return sessionRemaining < wait ? sessionRemaining : wait;
            }
        }

        private void CompletePending(BridgeControllerAsk ask) {
            lock (gate) {
                // The interaction is over, so the deadline that guarded it must not end the next one early, and a
                // message that belongs to it must not be taken for an answer to the next measurement.
                answerDeadline = null;
                lostAfterSilenceDeadline = null;
                pendingInteraction = PendingInteraction.None;
            }

            pending.TrySetResult(ask);
        }

        /// <summary>
        /// Raised once when the controller ends the run, so the alignment loop can tell the operator what happened
        /// with the name the controller announced itself.
        /// </summary>
        public event EventHandler<BridgeRunFailedEventArgs> RunFailed;

        /// <summary>
        /// Raised when the controller announces the move it is about to make, so the alignment loop can show what
        /// the controller is doing while it holds the capture window.
        /// </summary>
        public event EventHandler<BridgeAdjustmentRequestPayload> AdjustmentAnnounced;

        /// <summary>
        /// The single failure path of the run: the controller ended it, so the session is closed, the run token is
        /// cancelled - which aborts whatever TPPA is doing right now, in every phase - and the pending interaction
        /// is failed so its waiter throws.
        /// </summary>
        private void Fail(string kind, string reason, string note, bool isFault) {
            lock (gate) {
                if (failure != null || ended) { return; }
                failure = new OperationCanceledException($"The external correction run ended: {reason}.");
            }

            Logger.Warning($"[Bridge] {kind} from the controller while the external correction was active: {reason}" +
                           (string.IsNullOrWhiteSpace(note) ? "." : $" ({note})."));
            RaiseRunFailed(reason, note, isFault, controllerStoppedAnswering: false);

            EndRun(reason, note);
        }

        /// <summary>
        /// Ends the run because the controller stopped answering: silence does not prove that its axes stopped, so
        /// the run is cancelled instead of continuing with TPPA's own captures.
        /// </summary>
        private void FailLost(string reason) {
            lock (gate) {
                if (failure != null || ended) { return; }
                failure = new OperationCanceledException("The external controller stopped answering.");
            }

            Logger.Warning($"[Bridge] The controller stopped answering while the external correction was active: {reason}.");
            RaiseRunFailed(reason, "The controller stopped answering.", isFault: false, controllerStoppedAnswering: true);

            EndRun(reason, "The controller stopped answering.");
        }

        private void EndRun(string reason, string note) {
            _ = EndAsync(reason, false, new BridgeSessionEndedPayload { Detail = note }, CancellationToken.None);
            try { runTokens?.Cancel(); } catch (ObjectDisposedException) { }
            pending.TrySetResult(null);
        }

        private void RaiseRunFailed(string reason, string note, bool isFault, bool controllerStoppedAnswering) {
            var handler = RunFailed;
            if (handler == null) { return; }

            try {
                handler(this, new BridgeRunFailedEventArgs {
                    Reason = reason,
                    Note = note,
                    IsFault = isFault,
                    ControllerStoppedAnswering = controllerStoppedAnswering
                });
            } catch (Exception ex) {
                Logger.Error($"[Bridge] A run-failure listener failed: {ex.Message}");
            }
        }

        /// <summary>Publishes the measurement the run measured, so the controller can plan its move.</summary>
        private async Task PublishMeasurementAsync(BridgeMeasurementPayload payload, CancellationToken token) {
            if (payload == null) { return; }

            lock (gate) {
                samplesTaken++;
                lastMeasurementId = payload.MeasurementId;
                lastMeasurementUsable = string.Equals(payload.Status, BridgeMeasurementStatus.Valid, StringComparison.Ordinal);
                state = BridgeState.WaitingForRequest;
                stateReason = null;
                // The controller has nothing in its hands yet, so the answer has to arrive within the silence
                // timeout; once it announces a move it holds a capture window and the window deadlines take over.
                answerDeadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.SilenceTimeoutMs);
                lostAfterSilenceDeadline = null;
                // From here on exactly this measurement has to be answered: a move announced for an older one is stale.
                pendingInteraction = PendingInteraction.Adjustment;
            }

            payload.SessionId = SessionId;
            payload.SampleIndex = samplesTaken;
            payload.ToleranceArcMin = Options.ToleranceArcMin;
            payload.ContinuousEstimation = Options.ContinuousEstimation;

            await PublishAsync(BridgeKind.Measurement, payload, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Grants the capture window for the adjustment the controller announced. With <paramref name="existingWindowId"/>
        /// the grant of a window that is already open is repeated for a controller that repeated its announcement:
        /// no second window is opened and the adjustment is not announced again.
        /// </summary>
        private async Task GrantWindowAsync(BridgeAdjustmentRequestPayload adjustment, string existingWindowId = null) {
            var now = DateTimeOffset.UtcNow;
            var sessionRemaining = TimeSpan.FromSeconds(Options.SessionTimeoutSec) - RealRunningElapsed;
            var grant = new BridgeAdjustmentGrantPayload {
                WindowId = existingWindowId ?? Guid.NewGuid().ToString("N"),
                MeasurementId = adjustment?.MeasurementId,
                MaxWindowMs = (long)Math.Max(0, sessionRemaining.TotalMilliseconds),
                SilenceTimeoutMs = Options.SilenceTimeoutMs,
                GrantedAtUtc = now
            };

            if (existingWindowId == null) {
                lock (gate) {
                    windowId = grant.WindowId;
                    windowMeasurementId = grant.MeasurementId;
                    windowOpenedAt = now;
                    windowSilenceDeadline = now.AddMilliseconds(Options.SilenceTimeoutMs);
                    lostAfterSilenceDeadline = null;
                    // The controller answered by announcing its move: from here on the capture window it holds is what
                    // the run watches, because a long move is kept alive with keep-alives instead of an answer.
                    answerDeadline = null;
                    pendingInteraction = PendingInteraction.Completion;
                    state = BridgeState.WindowOpen;
                    stateReason = null;
                }

                RaiseAdjustmentAnnounced(adjustment);
            }

            await PublishAsync(BridgeKind.AdjustmentGranted, grant, CancellationToken.None).ConfigureAwait(false);
        }

        private void RaiseAdjustmentAnnounced(BridgeAdjustmentRequestPayload adjustment) {
            var handler = AdjustmentAnnounced;
            if (handler == null) { return; }

            try {
                handler(this, adjustment);
            } catch (Exception ex) {
                Logger.Error($"[Bridge] An adjustment listener failed: {ex.Message}");
            }
        }

        private void CloseWindow(string reason) {
            lock (gate) {
                CloseWindowLocked(reason);
            }
        }

        private void CloseWindowLocked(string reason) {
            windowId = null;
            windowMeasurementId = null;
            windowOpenedAt = default;
            windowSilenceDeadline = null;
            lostAfterSilenceDeadline = null;
            if (!ended) {
                state = BridgeState.Measuring;
                stateReason = reason;
            }
        }

        private void RefreshSilence() {
            lock (gate) {
                lostAfterSilenceDeadline = null;
                if (windowId != null) {
                    windowSilenceDeadline = DateTimeOffset.UtcNow.AddMilliseconds(Options.SilenceTimeoutMs);
                }
            }
        }

        private Task PublishAsync(string kind, object payload, CancellationToken token) {
            var envelope = BridgeEnvelope.Create(
                kind: kind,
                sessionId: SessionId,
                commandId: Guid.NewGuid().ToString("N"),
                replyTo: null,
                sequenceNumber: Interlocked.Increment(ref sequence),
                recipient: controllerAddress,
                payload: payload);
            return broker.Publish(new BridgeEventMessage(envelope));
        }

        private void OnHeartbeat(object state) {
            if (disposed || ended) { return; }
            Task.Run(async () => {
                try {
                    await PublishSessionStateAsync(null, null, CancellationToken.None).ConfigureAwait(false);
                } catch (Exception ex) {
                    Logger.Error($"[Bridge] Heartbeat failed: {ex.Message}");
                }
            });
        }

        private long sequence;
    }
}
