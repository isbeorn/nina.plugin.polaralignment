using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.External {

    /// <summary>
    /// Always-on external-correction endpoint of the plugin. It owns the single subscription to the
    /// command topic so a controller announcing itself is answered even before the user starts a
    /// session, and it routes session-scoped messages into the active
    /// <see cref="ExternalCorrectionSession"/>.
    /// </summary>
    public sealed class ExternalCorrectionHub : ISubscriber, IDisposable {
        /// <summary>
        /// How long a controller may stay quiet before TPPA considers it gone. A controller announces
        /// itself every few seconds while it is connected, so this timeout IS the handshake: no user
        /// setting is needed and TPPA keeps running normally when no controller is around.
        /// </summary>
        public static readonly TimeSpan ControllerPresenceWindow = TimeSpan.FromSeconds(15);

        private readonly IMessageBroker broker;
        private readonly object gate = new object();
        private ExternalCorrectionSession session;
        private DateTimeOffset? lastControllerSeenAt;
        private string controllerName;
        private string controllerVersion;
        private bool disposed;

        private ExternalCorrectionHub(IMessageBroker broker) {
            this.broker = broker;
            broker.Subscribe(ExternalCorrectionContract.CommandTopic, this);
        }

        /// <summary>Hub created once by the plugin manifest; null while the plugin is not loaded.</summary>
        public static ExternalCorrectionHub Instance { get; private set; }

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

        public static ExternalCorrectionHub EnsureInitialized(IMessageBroker broker) {
            if (Instance != null || broker == null) { return Instance; }
            Instance = new ExternalCorrectionHub(broker);
            Logger.Info("[ExternalCorrection] External correction endpoint initialized.");
            return Instance;
        }

        public static void Shutdown() {
            var hub = Instance;
            Instance = null;
            hub?.Dispose();
        }

        /// <summary>The session currently owning the capture loop, if any.</summary>
        public ExternalCorrectionSession Session {
            get { lock (gate) { return session; } }
        }

        public void AttachSession(ExternalCorrectionSession newSession) {
            lock (gate) { session = newSession; }
        }

        public void DetachSession(ExternalCorrectionSession oldSession) {
            lock (gate) {
                if (ReferenceEquals(session, oldSession)) { session = null; }
            }
        }

        public Task OnMessageReceived(IMessage message) {
            if (message == null || disposed) { return Task.CompletedTask; }

            var envelope = ExternalCorrectionEnvelope.FromJson(message.Content as string);
            if (envelope == null) {
                Logger.Warning("[ExternalCorrection] Received a malformed controller message.");
                return Task.CompletedTask;
            }

            if (envelope.Version != ExternalCorrectionContract.InterfaceVersion) {
                Logger.Warning($"[ExternalCorrection] Controller uses interface version {envelope.Version}, TPPA has {ExternalCorrectionContract.InterfaceVersion}.");
                return Task.CompletedTask;
            }

            lock (gate) {
                lastControllerSeenAt = DateTimeOffset.UtcNow;
            }

            if (string.Equals(envelope.Kind, ExternalCorrectionKind.Capabilities, StringComparison.Ordinal)) {
                var announce = envelope.PayloadAs<ExternalCapabilitiesAnnouncePayload>();
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
                Logger.Debug($"[ExternalCorrection] Ignoring '{envelope.Kind}' because no session is running.");
                return Task.CompletedTask;
            }

            // Queue the message and return immediately: the broker awaits this call, and the
            // alignment loop may be busy for minutes inside a capture or a move.
            activeSession.HandleEnvelope(envelope);
            return Task.CompletedTask;
        }

        public Task PublishCapabilitiesAsync(CancellationToken token) {
            var activeSession = Session;
            var options = activeSession?.Options ?? new ExternalCorrectionOptions();

            var payload = new ExternalCapabilitiesPayload {
                Controller = ExternalCorrectionContract.TppaRecipient,
                TppaVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                InterfaceVersion = ExternalCorrectionContract.InterfaceVersion,
                SupportedKinds = ExternalCorrectionKind.All,
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

            var envelope = ExternalCorrectionEnvelope.Create(
                kind: ExternalCorrectionKind.Capabilities,
                sessionId: activeSession?.SessionId,
                commandId: Guid.NewGuid().ToString("N"),
                replyTo: null,
                sequenceNumber: 0,
                recipient: ExternalCorrectionContract.ControllerRecipient,
                payload: payload);

            Logger.Debug("[ExternalCorrection] Answered controller capabilities announcement.");
            return broker.Publish(new ExternalCorrectionEventMessage(envelope));
        }

        public void Dispose() {
            if (disposed) { return; }
            disposed = true;
            try {
                broker?.Unsubscribe(ExternalCorrectionContract.CommandTopic, this);
            } catch (Exception ex) {
                Logger.Error($"[ExternalCorrection] Failed to unsubscribe: {ex.Message}");
            }
        }
    }
}
