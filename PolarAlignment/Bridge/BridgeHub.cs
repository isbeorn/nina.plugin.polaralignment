using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Bridge {

    /// <summary>
    /// Always-on external-correction endpoint of the plugin. It owns the single subscription to the
    /// command topic so a controller announcing itself is answered even before the user starts a
    /// session, and it routes session-scoped messages into the active
    /// <see cref="BridgeControllerRun"/>.
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
        private readonly Dictionary<Guid, RegisteredController> registrations = new Dictionary<Guid, RegisteredController>();
        private BridgeControllerRun session;

        /// <summary>Broker identity of the controller the running session belongs to, if any.</summary>
        private Guid? ownerId;

        private bool disposed;

        private BridgeHub(IMessageBroker broker) {
            this.broker = broker;
            broker.Subscribe(BridgeContract.CommandTopic, this);
        }

        /// <summary>Hub created once by the plugin manifest; null while the plugin is not loaded.</summary>
        public static BridgeHub Instance { get; private set; }

        /// <summary>
        /// A controller that announced itself on the broker. <see cref="SenderId"/> is the identity that decides
        /// whether a message belongs to the controller that owns the run, while the announced name is both what
        /// the operator sees in the system list and the address TPPA publishes to.
        /// </summary>
        public sealed class RegisteredController {
            public Guid SenderId { get; set; }
            public string Name { get; set; }
            public string Version { get; set; }
            public DateTimeOffset LastSeenAt { get; set; }

            /// <summary>Name and version as one label, e.g. "MLAstroRPA 3.1.3.0".</summary>
            public string Display => string.IsNullOrWhiteSpace(Name)
                ? "The external alignment controller"
                : (string.IsNullOrWhiteSpace(Version) ? Name : $"{Name} {Version}");
        }

        /// <summary>
        /// Controllers seen on the broker inside the presence window, newest first. This is the list the system
        /// selection shows: a controller that switches its assignment off leaves it again.
        /// </summary>
        public IReadOnlyList<RegisteredController> RegisteredControllers {
            get {
                lock (gate) {
                    PruneExpiredControllers();
                    return registrations.Values.OrderByDescending(controller => controller.LastSeenAt).ToList();
                }
            }
        }

        /// <summary>
        /// Raised as soon as a controller registers itself on the broker, so the system selection shows it
        /// right away instead of on its next refresh. A controller that stops answering is only detectable by
        /// the presence window, so leaving the list stays a time based check.
        /// </summary>
        public event EventHandler RegisteredControllersChanged;

        /// <summary>Announces a registration without letting a listener run inside the lock.</summary>
        private void RaiseRegisteredControllersChanged() {
            var handler = RegisteredControllersChanged;
            if (handler == null) { return; }

            try {
                handler(this, EventArgs.Empty);
            } catch (Exception ex) {
                Logger.Warning($"[Bridge] A registered-controller listener failed: {ex.Message}");
            }
        }

        /// <summary>True while the controller with this broker identity is present.</summary>
        public bool IsRegistered(Guid? senderId) {
            if (!senderId.HasValue || senderId.Value == Guid.Empty) { return false; }

            lock (gate) {
                PruneExpiredControllers();
                return registrations.ContainsKey(senderId.Value);
            }
        }

        /// <summary>
        /// True while the controller TPPA is working with is present. Before a run takes the broker over that is
        /// any registered controller; while a run owns it, it is that run's controller only, so a second
        /// controller cannot keep a session alive after its owner went quiet.
        /// </summary>
        public bool IsControllerPresent {
            get {
                lock (gate) {
                    PruneExpiredControllers();
                    return ownerId.HasValue ? registrations.ContainsKey(ownerId.Value) : registrations.Count > 0;
                }
            }
        }

        /// <summary>
        /// The controller TPPA is working with: the owner of the running session, otherwise the controller that
        /// announced itself last, otherwise null when none is registered.
        /// </summary>
        private RegisteredController CurrentController() {
            lock (gate) {
                PruneExpiredControllers();
                if (ownerId.HasValue && registrations.TryGetValue(ownerId.Value, out var owner)) { return owner; }
                return registrations.Values.OrderByDescending(controller => controller.LastSeenAt).FirstOrDefault();
            }
        }

        /// <summary>Drops controllers that stopped answering, so the system list follows the broker.</summary>
        private void PruneExpiredControllers() {
            var cutoff = DateTimeOffset.UtcNow - ControllerPresenceWindow;
            foreach (var key in registrations.Where(entry => entry.Value.LastSeenAt < cutoff)
                                             .Select(entry => entry.Key)
                                             .ToList()) {
                registrations.Remove(key);
            }
        }

        /// <summary>
        /// Removes a controller that said goodbye, so it leaves the system list without waiting for the presence
        /// window. Returns true when a controller was removed.
        /// </summary>
        private bool Deregister(Guid senderId) {
            if (senderId == Guid.Empty) { return false; }

            lock (gate) {
                return registrations.Remove(senderId);
            }
        }

        /// <summary>
        /// Identity of a controller that fills no <see cref="IMessage.SenderId"/> (an older build): a stable value
        /// derived from the name it announced, so it still appears in the system selection and can be chosen. A
        /// message that identifies no sender is never filtered, which is what keeps such a controller working.
        /// </summary>
        private static Guid DeriveIdentity(string name) {
            using (var md5 = System.Security.Cryptography.MD5.Create()) {
                var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("TPPA:controller:" + (name ?? string.Empty)));
                return new Guid(hash);
            }
        }

        /// <summary>
        /// Name and version of the controller TPPA is working with, e.g. "MLAstroRPA 2.2.0.0". Every status text
        /// and notification that talks about the controller uses this, so the operator sees which plugin is on
        /// the other end instead of a generic wording. A controller that has not identified itself yet falls back
        /// to the generic wording.
        /// </summary>
        public string ControllerDisplay => CurrentController()?.Display ?? "the external alignment controller";

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
        /// Address TPPA puts in <c>IntendedRecipient</c> of every message it sends to the controller: the name
        /// that controller announced itself with, so controllers of different vendors never share one address.
        /// Until a controller has identified itself the MLAstro default is used, which keeps a controller that
        /// announces no name (an older build) working.
        /// </summary>
        public string ControllerAddress {
            get {
                var controller = CurrentController();
                return string.IsNullOrWhiteSpace(controller?.Name) ? BridgeContract.ControllerRecipient : controller.Name;
            }
        }

        /// <summary>The external correction run currently owning the capture loop, if any.</summary>
        public BridgeControllerRun Session {
            get { lock (gate) { return session; } }
        }

        /// <summary>Broker identity of the controller the running session belongs to, if any.</summary>
        public Guid? SessionOwnerId {
            get { lock (gate) { return ownerId; } }
        }

        /// <summary>
        /// Hands the broker over to a run. The controller the operator selected becomes the owner of the run: its
        /// identity and announced address are copied into the run and are never refreshed afterwards, so the run
        /// keeps talking to that controller and nobody else can take it over.
        /// </summary>
        public void AttachSession(BridgeControllerRun newSession, Guid? selectedControllerId) {
            string address;
            lock (gate) {
                session = newSession;
                ownerId = selectedControllerId;
                if (selectedControllerId.HasValue && registrations.TryGetValue(selectedControllerId.Value, out var selected)) {
                    address = string.IsNullOrWhiteSpace(selected.Name) ? BridgeContract.ControllerRecipient : selected.Name;
                } else {
                    address = BridgeContract.ControllerRecipient;
                }
            }
            newSession.BindOwner(selectedControllerId, address);
        }

        public void DetachSession(BridgeControllerRun oldSession) {
            lock (gate) {
                if (ReferenceEquals(session, oldSession)) {
                    session = null;
                    ownerId = null;
                }
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

            var announce = string.Equals(envelope.Kind, BridgeKind.Capabilities, StringComparison.Ordinal)
                ? envelope.PayloadAs<BridgeCapabilitiesAnnouncePayload>()
                : null;

            var foreignAnnounce = false;
            var registered = false;
            string announceAddress = null;
            lock (gate) {
                // A controller that fills no sender id (an older build) is identified by the name it announced,
                // so it can still appear in the system selection and be chosen by the operator.
                var identity = message.SenderId != Guid.Empty
                    ? message.SenderId
                    : (announce != null ? DeriveIdentity(announce.Controller) : (Guid?)null);

                if (identity.HasValue) {
                    if (!registrations.TryGetValue(identity.Value, out var controller)) {
                        controller = new RegisteredController { SenderId = identity.Value };
                        registrations[identity.Value] = controller;
                        registered = true;
                    }

                    controller.LastSeenAt = DateTimeOffset.UtcNow;
                    if (announce != null) {
                        controller.Name = announce.Controller;
                        controller.Version = announce.ControllerVersion;
                        // The answer belongs to the controller that announced itself, not to whichever controller
                        // announced last: with two controllers on the broker (two MLAstro builds) each one has to
                        // receive its own capabilities instead of traffic meant for the other one. A controller that
                        // announced no name is answered under the MLAstro default it is addressed with.
                        announceAddress = string.IsNullOrWhiteSpace(announce.Controller)
                            ? BridgeContract.ControllerRecipient
                            : announce.Controller;
                    }

                    // The owner of a running session never changes: a second controller that announces itself
                    // only adds itself to the list the operator selects from.
                    foreignAnnounce = announce != null && ownerId.HasValue && identity.Value != ownerId.Value;
                }
                PruneExpiredControllers();
            }

            if (registered) {
                RaiseRegisteredControllersChanged();
            }

            if (foreignAnnounce) {
                Logger.Info($"[Bridge] Another controller announced itself while a run is in progress. The run stays with {ControllerDisplay}.");
            }

            if (announce != null) {
                return PublishCapabilitiesAsync(CancellationToken.None, announceAddress);
            }

            // A controller that switches its broker off says goodbye instead of falling silent: dropping it right
            // away keeps the system list honest, whether a run is in progress or not.
            if (string.Equals(envelope.Kind, BridgeKind.Cancel, StringComparison.Ordinal)
                && string.Equals(envelope.PayloadAs<BridgeCancelPayload>()?.Reason, BridgeReason.BrokerDisabled, StringComparison.Ordinal)
                && Deregister(message.SenderId)) {
                Logger.Info("[Bridge] The controller switched its broker off and left the alignment system list.");
                RaiseRegisteredControllersChanged();
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
        /// Reports a message from a controller that does not own the running session, so the log explains why
        /// a second controller has no effect instead of leaving the operator with a silence timeout.
        /// </summary>
        private void ReportForeignController(IMessage message, BridgeEnvelope envelope) {
            lock (gate) {
                if (!unexpectedSenders.Add(message.SenderId)) { return; }
            }
            Logger.Warning($"[Bridge] Ignored a '{envelope.Kind}' message from a controller that does not own the running session.");
        }

        /// <summary>
        /// Answers a controller announcement. The reply goes to <paramref name="recipient"/> when the caller knows
        /// who announced itself, because more than one controller can be registered at the same time.
        /// </summary>
        public Task PublishCapabilitiesAsync(CancellationToken token, string recipient = null) {
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
                recipient: recipient ?? ControllerAddress,
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
