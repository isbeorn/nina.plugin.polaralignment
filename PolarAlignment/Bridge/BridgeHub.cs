using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Bridge {

    /// <summary>
    /// Always-on external-correction endpoint of the plugin. It owns the single subscription to the
    /// command topic so a controller announcing itself is answered even before the user starts a
    /// session, and it routes session-scoped messages into the active
    /// <see cref="BridgeSession"/>.
    /// </summary>
    public sealed class BridgeHub : ISubscriber, IDisposable {
        /// <summary>
        /// How long a controller may stay quiet before TPPA considers it gone. A controller announces
        /// itself every few seconds while it is connected, so this timeout IS the handshake: no user
        /// setting is needed and TPPA keeps running normally when no controller is around.
        /// </summary>
        public static readonly TimeSpan ControllerPresenceWindow = TimeSpan.FromSeconds(8);

        private readonly IMessageBroker broker;
        private readonly object gate = new object();
        private BridgeSession session;
        private DateTimeOffset? lastControllerSeenAt;

        /// <summary>
        /// When the registered controller was last seen. While a run owns the broker this is the timestamp
        /// that counts, so a second controller cannot keep a session alive after its owner went quiet.
        /// </summary>
        private DateTimeOffset? lastOwnerSeenAt;

        /// <summary>
        /// Broker identity of the registered controller (<see cref="IMessage.SenderId"/>). It is refreshed
        /// while no run owns the broker - so the next run binds whoever is present - and frozen once a run
        /// has taken it over, which is what keeps a second controller from stealing the running session.
        /// </summary>
        private Guid? controllerSenderId;
        private string controllerName;
        private string controllerVersion;
        private bool disposed;

        private BridgeHub(IMessageBroker broker) {
            this.broker = broker;
            broker.Subscribe(BridgeContract.CommandTopic, this);
        }

        /// <summary>Hub created once by the plugin manifest; null while the plugin is not loaded.</summary>
        public static BridgeHub Instance { get; private set; }

        /// <summary>
        /// True while the controller TPPA is working with announced itself within the presence window. Before
        /// a run takes the broker over that is any controller - the mode is detected, not configured - and
        /// while a run owns the broker it is that run's controller only. The external correction mode only
        /// turns on while this is true, so a run without a controller behaves exactly like it always did.
        /// </summary>
        public bool IsControllerPresent {
            get {
                lock (gate) {
                    var seenAt = controllerSenderId.HasValue ? lastOwnerSeenAt : lastControllerSeenAt;
                    return seenAt.HasValue
                           && DateTimeOffset.UtcNow - seenAt.Value <= ControllerPresenceWindow;
                }
            }
        }

        /// <summary>True when the message comes from the controller currently registered on the broker.</summary>
        private bool IsRegisteredSender(Guid senderId) {
            lock (gate) {
                return controllerSenderId.HasValue && senderId != Guid.Empty && senderId == controllerSenderId.Value;
            }
        }

        /// <summary>
        /// Name and version of the controller that last announced itself, e.g. "MLAstroRPA 2.2.0.0". Every
        /// status text and notification that talks about the controller uses this, so the operator sees
        /// which plugin is on the other end instead of a generic wording. A controller that has not
        /// identified itself yet falls back to the generic wording.
        /// </summary>
        public string ControllerDisplay {
            get {
                lock (gate) {
                    if (string.IsNullOrWhiteSpace(controllerName)) { return "the external alignment controller"; }
                    return string.IsNullOrWhiteSpace(controllerVersion) ? controllerName : $"{controllerName} {controllerVersion}";
                }
            }
        }

        /// <summary><see cref="ControllerDisplay"/> written so it can start a sentence.</summary>
        public string ControllerDisplayCapitalized {
            get {
                var display = ControllerDisplay;
                return char.ToUpperInvariant(display[0]) + display.Substring(1);
            }
        }

        public static BridgeHub EnsureInitialized(IMessageBroker broker) {
            if (Instance != null || broker == null) { return Instance; }
            Instance = new BridgeHub(broker);
            Logger.Info("[Bridge] External correction endpoint initialized.");
            return Instance;
        }

        public static void Shutdown() {
            var hub = Instance;
            Instance = null;
            hub?.Dispose();
        }

        /// <summary>
        /// Address TPPA puts in <c>IntendedRecipient</c> of every message it sends to the controller: the
        /// name the controller announced itself with, so a controller of any vendor is addressed with its
        /// own name and a controller that filters on the field keeps working. Until a controller has
        /// identified itself the MLAstro default is used, which keeps a controller that announces no name
        /// (an older build) working.
        /// </summary>
        public string ControllerAddress {
            get {
                lock (gate) {
                    return string.IsNullOrWhiteSpace(controllerName) ? BridgeContract.ControllerRecipient : controllerName;
                }
            }
        }

        /// <summary>The session currently owning the capture loop, if any.</summary>
        public BridgeSession Session {
            get { lock (gate) { return session; } }
        }

        /// <summary>
        /// Hands the broker over to a run. The controller registered at this moment becomes the owner of the
        /// run: its identity and address are copied into the session and are never refreshed afterwards, so
        /// the run keeps talking to the controller it started with.
        /// </summary>
        public void AttachSession(BridgeSession newSession) {
            Guid? ownerId;
            string address;
            lock (gate) {
                session = newSession;
                ownerId = controllerSenderId;
                lastOwnerSeenAt = lastControllerSeenAt;
                address = ControllerAddress;
            }
            newSession.BindOwner(ownerId, address);
        }

        public void DetachSession(BridgeSession oldSession) {
            lock (gate) {
                if (ReferenceEquals(session, oldSession)) { session = null; }
            }
        }

        public Task OnMessageReceived(IMessage message) {
            if (message == null || disposed) { return Task.CompletedTask; }

            var envelope = BridgeEnvelope.FromJson(message.Content as string);
            if (envelope == null) {
                Logger.Warning("[Bridge] Received a malformed controller message.");
                return Task.CompletedTask;
            }

            if (envelope.Version != BridgeContract.InterfaceVersion) {
                Logger.Warning($"[Bridge] Controller uses interface version {envelope.Version}, TPPA has {BridgeContract.InterfaceVersion}.");
                return Task.CompletedTask;
            }

            lock (gate) {
                lastControllerSeenAt = DateTimeOffset.UtcNow;

                // Registration: while no run owns the broker the last controller seen is the one the next run
                // binds. With a run in progress the owner is frozen, so this never changes under it.
                if (session == null && message.SenderId != Guid.Empty) {
                    controllerSenderId = message.SenderId;
                }
            }

            if (IsRegisteredSender(message.SenderId)) {
                lock (gate) { lastOwnerSeenAt = DateTimeOffset.UtcNow; }
            }

            if (string.Equals(envelope.Kind, BridgeKind.Capabilities, StringComparison.Ordinal)) {
                var announce = envelope.PayloadAs<BridgeCapabilitiesAnnouncePayload>();
                if (announce != null) {
                    RegisterController(message, announce);
                }
                return PublishCapabilitiesAsync(CancellationToken.None);
            }

            var activeSession = Session;
            if (activeSession == null) {
                Logger.Debug($"[Bridge] Ignoring '{envelope.Kind}' because no session is running.");
                return Task.CompletedTask;
            }

            // The run belongs to the controller it started with: a message from anybody else is reported once
            // and dropped, so a second controller can neither measure for nor cancel somebody else's run.
            if (!activeSession.AcceptsSender(message.SenderId)) {
                ReportForeignController(message, envelope);
                return Task.CompletedTask;
            }

            // Queue the message and return immediately: the broker awaits this call, and the
            // alignment loop may be busy for minutes inside a capture or a move.
            activeSession.HandleEnvelope(envelope);
            return Task.CompletedTask;
        }

        /// <summary>Senders already reported for a foreign message, so each of them is written to the log once.</summary>
        private readonly HashSet<Guid> unexpectedSenders = new HashSet<Guid>();

        /// <summary>
        /// Registers an announced controller. While the broker is free the announced name becomes the address
        /// TPPA answers to and the next run binds that controller. An announcement that arrives while a run
        /// owns the broker only proves the controller is still alive: the owner itself is never reassigned,
        /// which is what keeps a second controller from taking a running session over.
        /// </summary>
        private void RegisterController(IMessage message, BridgeCapabilitiesAnnouncePayload announce) {
            var reported = false;
            lock (gate) {
                if (session != null) {
                    reported = controllerSenderId.HasValue && message.SenderId != Guid.Empty
                               && message.SenderId != controllerSenderId.Value
                               && unexpectedSenders.Add(message.SenderId);
                } else {
                    controllerName = announce.Controller;
                    controllerVersion = announce.ControllerVersion;
                }
            }

            if (reported) {
                Logger.Warning($"[Bridge] Ignored an announcement from another controller while a run is in progress: {ControllerDisplay} owns it.");
            }
        }

        /// <summary>
        /// Reports a message from a controller that does not own the running session, so the log explains why
        /// a second controller has no effect instead of leaving the operator with a silence timeout.
        /// </summary>
        private void ReportForeignController(IMessage message, BridgeEnvelope envelope) {
            lock (gate) {
                if (!unexpectedSenders.Add(message.SenderId)) { return; }
            }
            Logger.Warning($"[Bridge] Ignored a '{envelope.Kind}' message from a controller that does not own the running session.");
        }

        public Task PublishCapabilitiesAsync(CancellationToken token) {
            var activeSession = Session;
            var options = activeSession?.Options ?? new BridgeOptions();

            var payload = new BridgeCapabilitiesPayload {
                Controller = BridgeContract.TppaRecipient,
                TppaVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                InterfaceVersion = BridgeContract.InterfaceVersion,
                SupportedKinds = BridgeKind.All,
                SessionActive = activeSession?.IsActive == true,
                ActiveSessionId = activeSession?.IsActive == true ? activeSession.SessionId : null,
                ToleranceArcMin = options.ToleranceArcMin > 0
                    ? options.ToleranceArcMin
                    : Properties.Settings.Default.AlignmentTolerance,
                AutoFinishConditionAvailable = true,
                HeartbeatMs = options.HeartbeatMs,
                SilenceTimeoutMs = options.SilenceTimeoutMs,
                ReadyTimeoutMs = options.ReadyTimeoutMs,
                SessionTimeoutSec = options.SessionTimeoutSec,
                GraceAfterSilenceMs = options.GraceAfterSilenceMs,
                StopAckTimeoutMs = options.StopAckTimeoutMs,
                ContinuousEstimation = Properties.Settings.Default.UseContinuousErrorEstimator
            };

            var envelope = BridgeEnvelope.Create(
                kind: BridgeKind.Capabilities,
                sessionId: activeSession?.SessionId,
                commandId: Guid.NewGuid().ToString("N"),
                replyTo: null,
                sequenceNumber: 0,
                recipient: ControllerAddress,
                payload: payload);

            Logger.Debug("[Bridge] Answered controller capabilities announcement.");
            return broker.Publish(new BridgeEventMessage(envelope));
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            try {
                broker?.Unsubscribe(BridgeContract.CommandTopic, this);
            } catch (Exception ex) {
                Logger.Error($"[Bridge] Failed to unsubscribe: {ex.Message}");
            }
        }
    }
}
