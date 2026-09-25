using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
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
        public static readonly TimeSpan ControllerPresenceWindow = TimeSpan.FromSeconds(15);

        private readonly IMessageBroker broker;
        private readonly object gate = new object();
        private BridgeSession session;
        private DateTimeOffset? lastControllerSeenAt;
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
        /// True while a controller announced itself within the presence window. The external correction
        /// mode only turns on while this is true, so a run without a controller behaves exactly like it
        /// always did. Any controller message counts, not just an announcement.
        /// </summary>
        public bool IsControllerPresent {
            get {
                lock (gate) {
                    return lastControllerSeenAt.HasValue
                           && DateTimeOffset.UtcNow - lastControllerSeenAt.Value <= ControllerPresenceWindow;
                }
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

        /// <summary>The session currently owning the capture loop, if any.</summary>
        public BridgeSession Session {
            get { lock (gate) { return session; } }
        }

        public void AttachSession(BridgeSession newSession) {
            lock (gate) { session = newSession; }
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
            }

            if (string.Equals(envelope.Kind, BridgeKind.Capabilities, StringComparison.Ordinal)) {
                var announce = envelope.PayloadAs<BridgeCapabilitiesAnnouncePayload>();
                if (announce != null) {
                    lock (gate) {
                        controllerName = announce.Controller;
                        controllerVersion = announce.ControllerVersion;
                    }
                }
                return PublishCapabilitiesAsync(CancellationToken.None);
            }

            var activeSession = Session;
            if (activeSession == null) {
                Logger.Debug($"[Bridge] Ignoring '{envelope.Kind}' because no session is running.");
                return Task.CompletedTask;
            }

            // Queue the message and return immediately: the broker awaits this call, and the
            // alignment loop may be busy for minutes inside a capture or a move.
            activeSession.HandleEnvelope(envelope);
            return Task.CompletedTask;
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
                recipient: BridgeContract.ControllerRecipient,
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
