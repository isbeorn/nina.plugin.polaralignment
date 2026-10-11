using FluentAssertions;
using NINA.Core.Model;
using NINA.Plugin.Interfaces;
using NINA.Plugins.PolarAlignment.Bridge;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Tests of the external correction step: the run helper publishes a measurement and waits for the single
    /// answer of the controller, and every way of ending badly goes through the same failure path - the session is
    /// closed, the run token is cancelled and the pending wait throws. The camera and mount loop lives in the
    /// sequence item, so it is not exercised here.
    /// </summary>
    [TestFixture]
    public class BridgeControllerRunTest {

        private sealed class FakeMessageBroker : IMessageBroker {
            private readonly object gate = new object();
            private readonly List<BridgeEnvelope> published = new List<BridgeEnvelope>();

            public IReadOnlyList<BridgeEnvelope> Published {
                get { lock (gate) { return published.ToList(); } }
            }

            public BridgeEnvelope Last(string kind) {
                lock (gate) {
                    return published.LastOrDefault(e => e.Kind == kind);
                }
            }

            public int Count(string kind) {
                lock (gate) {
                    return published.Count(e => e.Kind == kind);
                }
            }

            public Task Publish(IMessage message) {
                var envelope = BridgeEnvelope.FromJson(message.Content as string);
                lock (gate) { published.Add(envelope); }
                return Task.CompletedTask;
            }

            public void Subscribe(string topic, ISubscriber subscriber) { }

            public void Unsubscribe(string topic, ISubscriber subscriber) { }
        }

        private static BridgeOptions FastOptions(int? silenceTimeoutMs = null, int? graceMs = null, int sessionTimeoutSec = 3600) {
            return new BridgeOptions {
                HeartbeatMs = 100000,
                SilenceTimeoutMs = silenceTimeoutMs ?? 400,
                ReadyTimeoutMs = 500,
                GraceAfterSilenceMs = graceMs ?? 200,
                SessionTimeoutSec = sessionTimeoutSec,
                StopAckTimeoutMs = 400,
                ToleranceArcMin = 1.0
            };
        }

        private static BridgeControllerRun NewRun(FakeMessageBroker broker, CancellationTokenSource runTokens, BridgeOptions options = null) {
            var run = new BridgeControllerRun(broker, runTokens, "session-1");
            run.ApplyOptions(options ?? FastOptions());
            return run;
        }

        private static BridgeEnvelope Command(string kind, object payload = null, string sessionId = "session-1") {
            return BridgeEnvelope.Create(kind,
                                                     sessionId,
                                                     Guid.NewGuid().ToString("N"),
                                                     null,
                                                     1,
                                                     BridgeContract.TppaRecipient,
                                                     payload);
        }

        private static BridgeMeasurementPayload Measurement() {
            return new BridgeMeasurementPayload {
                MeasurementId = Guid.NewGuid().ToString("N"),
                Status = BridgeMeasurementStatus.Valid,
                AzimuthErrorArcMin = 5,
                AltitudeErrorArcMin = 3,
                TotalErrorArcMin = 5.83,
                ToleranceArcMin = 1,
                ToleranceReached = false,
                Northern = true,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }

        /// <summary>Waits for a published message or for a state the run reports at its own pace.</summary>
        private static async Task WaitUntilAsync(Func<bool> condition) {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline) {
                if (condition()) { return; }
                await Task.Delay(10);
            }

            Assert.Fail("The expected state was not reached within 5 seconds.");
        }

        [Test]
        public async Task Open_PublishesTheReferenceSweepState() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());

            await run.OpenAsync(1.0, false, CancellationToken.None);

            var state = broker.Last(BridgeKind.SessionState).PayloadAs<BridgeSessionStatePayload>();
            state.State.Should().Be(BridgeState.Measuring);
            state.Reason.Should().Be(BridgeReason.ReferenceSweep);
            broker.Last(BridgeKind.SessionState).SessionId.Should().Be("session-1");
        }

        [Test]
        public async Task MeasurementIsPublishedWithTheSessionAndSampleIndex() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());
            await run.OpenAsync(1.0, false, CancellationToken.None);

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), CancellationToken.None);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);
            run.HandleEnvelope(Command(BridgeKind.RequestMeasurement, new BridgeMeasurementRequestPayload { WindowId = "w1" }));

            (await wait).Kind.Should().Be(BridgeControllerAskKind.AdjustmentFinished);

            var measurement = broker.Last(BridgeKind.Measurement).PayloadAs<BridgeMeasurementPayload>();
            measurement.SessionId.Should().Be("session-1");
            measurement.SampleIndex.Should().Be(1);
            measurement.ToleranceArcMin.Should().Be(1.0);
        }

        [Test]
        public async Task AFinishedAdjustmentAnswersWithTheSettleTheControllerAsked() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());
            await run.OpenAsync(1.0, false, CancellationToken.None);

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), CancellationToken.None);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);
            run.HandleEnvelope(Command(BridgeKind.RequestMeasurement,
                                                            new BridgeMeasurementRequestPayload { WindowId = "w2", SettleSeconds = 2.5 }));

            var ask = await wait;
            ask.Kind.Should().Be(BridgeControllerAskKind.AdjustmentFinished);
            ask.WindowId.Should().Be("w2");
            ask.SettleSeconds.Should().Be(2.5);
        }

        [Test]
        public async Task ACompletionRequestAnswersWithAVerificationRequest() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());
            await run.OpenAsync(1.0, false, CancellationToken.None);

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), CancellationToken.None);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);
            run.HandleEnvelope(Command(BridgeKind.RequestCompletion, new BridgeCompletionRequestPayload { WindowId = "w3" }));

            var ask = await wait;
            ask.Kind.Should().Be(BridgeControllerAskKind.VerificationRequested);
            ask.WindowId.Should().Be("w3");
        }

        [Test]
        public async Task AnAnnouncedAdjustmentGrantsAWindowAndKeepsTheWaitPending() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());
            await run.OpenAsync(1.0, false, CancellationToken.None);

            BridgeAdjustmentRequestPayload announced = null;
            run.AdjustmentAnnounced += (_, adjustment) => announced = adjustment;

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), CancellationToken.None);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);
            run.HandleEnvelope(Command(BridgeKind.BeginAdjustment,
                                                            new BridgeAdjustmentRequestPayload { MeasurementId = "m1", PlannedAzimuthArcMin = 3, DetectingDirection = true }));

            // The grant does not answer the pending interaction: the move is still running.
            await WaitUntilAsync(() => broker.Count(BridgeKind.AdjustmentGranted) == 1);
            wait.IsCompleted.Should().BeFalse();
            run.CurrentWindowId.Should().NotBeNullOrEmpty();
            announced.Should().NotBeNull();
            announced.DetectingDirection.Should().BeTrue();

            run.HandleEnvelope(Command(BridgeKind.RequestMeasurement, new BridgeMeasurementRequestPayload()));
            (await wait).Kind.Should().Be(BridgeControllerAskKind.AdjustmentFinished);
        }

        [Test]
        public async Task ReadinessIsCachedWithoutAnsweringThePendingInteraction() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());
            await run.OpenAsync(1.0, false, CancellationToken.None);

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), CancellationToken.None);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);

            run.HandleEnvelope(Command(BridgeKind.ControllerReady,
                                                            new BridgeControllerReadyPayload { HardwareReady = true, HardwareConnected = true, Note = "idle" }));

            run.ControllerHardwareReady.Should().BeTrue();
            run.ControllerHardwareConnected.Should().BeTrue();
            run.ControllerReadyNote.Should().Be("idle");
            wait.IsCompleted.Should().BeFalse();

            run.HandleEnvelope(Command(BridgeKind.RequestMeasurement, new BridgeMeasurementRequestPayload()));
            await wait;
        }

        [Test]
        public async Task ACancelEndsTheRunCancelsTheRunTokenAndFailsTheWait() {
            var broker = new FakeMessageBroker();
            using var runTokens = new CancellationTokenSource();
            using var run = NewRun(broker, runTokens);
            await run.OpenAsync(1.0, false, CancellationToken.None);

            BridgeRunFailedEventArgs reported = null;
            run.RunFailed += (_, e) => reported = e;

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), runTokens.Token);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);

            run.HandleEnvelope(Command(BridgeKind.Cancel, new BridgeCancelPayload { Reason = BridgeReason.UserStop, Note = "operator pressed STOP" }));

            var cancelled = false;
            try { await wait; } catch (OperationCanceledException) { cancelled = true; }
            cancelled.Should().BeTrue("the pending interaction fails when the controller cancels the run");
            runTokens.IsCancellationRequested.Should().BeTrue();
            broker.Last(BridgeKind.SessionEnded).PayloadAs<BridgeSessionEndedPayload>().Reason.Should().Be(BridgeReason.UserStop);
            reported.Should().NotBeNull();
            reported.Reason.Should().Be(BridgeReason.UserStop);
            reported.Note.Should().Be("operator pressed STOP");
            reported.IsFault.Should().BeFalse();
        }

        [Test]
        public async Task AFaultKeepsWhatTheControllerReported() {
            var broker = new FakeMessageBroker();
            using var runTokens = new CancellationTokenSource();
            using var run = NewRun(broker, runTokens);
            await run.OpenAsync(1.0, false, CancellationToken.None);

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), runTokens.Token);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);

            run.HandleEnvelope(Command(BridgeKind.Fault, new BridgeFaultPayload { Reason = BridgeReason.FirmwareDisconnected, Detail = "the port is gone" }));

            var cancelled = false;
            try { await wait; } catch (OperationCanceledException) { cancelled = true; }
            cancelled.Should().BeTrue("a fault fails the pending interaction");
            runTokens.IsCancellationRequested.Should().BeTrue();
            run.HasControllerFault.Should().BeTrue();
            run.ControllerFaultReason.Should().Be(BridgeReason.FirmwareDisconnected);
            run.ControllerFaultDetail.Should().Be("the port is gone");
        }

        [Test]
        public async Task ACaptureWindowThatGoesSilentEndsTheRunAsLost() {
            var broker = new FakeMessageBroker();
            using var runTokens = new CancellationTokenSource();
            using var run = NewRun(broker, runTokens, FastOptions(silenceTimeoutMs: 120, graceMs: 120));
            await run.OpenAsync(1.0, false, CancellationToken.None);

            BridgeRunFailedEventArgs reported = null;
            run.RunFailed += (_, e) => reported = e;

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), runTokens.Token);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);
            run.HandleEnvelope(Command(BridgeKind.BeginAdjustment, new BridgeAdjustmentRequestPayload { MeasurementId = "m1" }));
            await WaitUntilAsync(() => run.CurrentWindowId != null);

            var cancelled = false;
            try { await wait; } catch (OperationCanceledException) { cancelled = true; }
            cancelled.Should().BeTrue("silence in the capture window fails the pending interaction");

            reported.Should().NotBeNull();
            reported.ControllerStoppedAnswering.Should().BeTrue();
            runTokens.IsCancellationRequested.Should().BeTrue();
        }

        [Test]
        public async Task KeepAliveExtendsTheCaptureWindow() {
            var broker = new FakeMessageBroker();
            using var runTokens = new CancellationTokenSource();
            using var run = NewRun(broker, runTokens, FastOptions(silenceTimeoutMs: 250, graceMs: 250));
            await run.OpenAsync(1.0, false, CancellationToken.None);

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), runTokens.Token);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);
            run.HandleEnvelope(Command(BridgeKind.BeginAdjustment, new BridgeAdjustmentRequestPayload { MeasurementId = "m1" }));
            await WaitUntilAsync(() => run.CurrentWindowId != null);

            // Two keep-alives over a period longer than the silence timeout keep the window and the wait alive.
            for (var i = 0; i < 4; i++) {
                await Task.Delay(120);
                run.HandleEnvelope(Command(BridgeKind.KeepAlive));
            }

            runTokens.IsCancellationRequested.Should().BeFalse();
            wait.IsCompleted.Should().BeFalse();
            run.CurrentWindowId.Should().NotBeNullOrEmpty();

            run.HandleEnvelope(Command(BridgeKind.RequestMeasurement, new BridgeMeasurementRequestPayload()));
            (await wait).Kind.Should().Be(BridgeControllerAskKind.AdjustmentFinished);
        }

        [Test]
        public async Task MessagesForAnotherSessionOrRecipientAreIgnored() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());
            await run.OpenAsync(1.0, false, CancellationToken.None);

            var wait = run.PublishMeasurementAndWaitAsync(Measurement(), CancellationToken.None);
            await WaitUntilAsync(() => broker.Count(BridgeKind.Measurement) == 1);

            run.HandleEnvelope(Command(BridgeKind.RequestMeasurement, new BridgeMeasurementRequestPayload(), sessionId: "another-session"));

            var forSomebodyElse = Command(BridgeKind.RequestMeasurement, new BridgeMeasurementRequestPayload());
            forSomebodyElse.IntendedRecipient = "AcmePolar";
            run.HandleEnvelope(forSomebodyElse);

            wait.IsCompleted.Should().BeFalse();

            run.HandleEnvelope(Command(BridgeKind.RequestMeasurement, new BridgeMeasurementRequestPayload()));
            (await wait).Kind.Should().Be(BridgeControllerAskKind.AdjustmentFinished);
        }

        [Test]
        public void OnlyTheBoundControllerIsAccepted() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());
            var owner = Guid.NewGuid();

            // Nothing is filtered while the controller does not identify its messages.
            run.AcceptsSender(Guid.NewGuid()).Should().BeTrue();

            run.BindOwner(owner, "AcmePolar");

            run.OwnerId.Should().Be(owner);
            run.ControllerAddress.Should().Be("AcmePolar");
            run.AcceptsSender(owner).Should().BeTrue();
            run.AcceptsSender(Guid.NewGuid()).Should().BeFalse();
            run.AcceptsSender(Guid.Empty).Should().BeTrue();
        }

        [Test]
        public async Task EndAsyncPublishesTheFinalMessage() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());
            await run.OpenAsync(1.0, false, CancellationToken.None);

            await run.EndAsync(BridgeReason.Completed, true, new BridgeSessionEndedPayload { SamplesUsed = 4, Detail = "done" }, CancellationToken.None);

            var ended = broker.Last(BridgeKind.SessionEnded).PayloadAs<BridgeSessionEndedPayload>();
            ended.Reason.Should().Be(BridgeReason.Completed);
            ended.Achieved.Should().BeTrue();
            ended.SamplesUsed.Should().Be(4);
            run.IsActive.Should().BeFalse();
        }

        [Test]
        public async Task RequestStopReturnsTheAcknowledgementOfTheController() {
            var broker = new FakeMessageBroker();
            using var run = NewRun(broker, new CancellationTokenSource());
            await run.OpenAsync(1.0, false, CancellationToken.None);

            var stop = run.RequestStopAsync(BridgeReason.UserStop, CancellationToken.None);
            await WaitUntilAsync(() => broker.Count(BridgeKind.StopRequested) == 1);
            run.HandleEnvelope(Command(BridgeKind.Stopped, new BridgeStoppedPayload { HardwareStopStatus = BridgeHardwareStopStatus.Ok }));

            (await stop).Should().Be(BridgeHardwareStopStatus.Ok);
        }

        [Test]
        public async Task ACancelBeforeTheFirstMeasurementStillCancelsTheRunToken() {
            // The run is created before the reference sweep, so a cancel has to reach the captures that are
            // running right now - that is what the run token is for.
            var broker = new FakeMessageBroker();
            using var runTokens = new CancellationTokenSource();
            using var run = NewRun(broker, runTokens);
            await run.OpenAsync(1.0, false, CancellationToken.None);

            run.HandleEnvelope(Command(BridgeKind.Cancel, new BridgeCancelPayload { Reason = BridgeReason.BrokerDisabled }));

            await WaitUntilAsync(() => runTokens.IsCancellationRequested);
            broker.Last(BridgeKind.SessionEnded).PayloadAs<BridgeSessionEndedPayload>().Reason.Should().Be(BridgeReason.BrokerDisabled);
        }

        /// <summary>
        /// Controller message with a sender of its own, so a test can play two controllers on the broker. The
        /// plugin's own message classes always report the TPPA plugin id.
        /// </summary>
        private sealed class ControllerMessage : IMessage {
            private readonly string json;

            public ControllerMessage(BridgeEnvelope envelope, Guid senderId) {
                json = envelope.ToJson();
                SenderId = senderId;
            }

            public Guid SenderId { get; }
            public string Sender => "test-controller";
            public DateTimeOffset SentAt => DateTime.UtcNow;
            public Guid MessageId => Guid.NewGuid();
            public DateTimeOffset? Expiration => null;
            public Guid? CorrelationId => null;
            public int Version => BridgeContract.InterfaceVersion;
            public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();
            public string Topic => BridgeContract.CommandTopic;
            public object Content => json;
        }

        private static BridgeEnvelope Announce(string controller, string version = "1.0.0") {
            return BridgeEnvelope.Create(BridgeKind.Capabilities,
                                                     null,
                                                     Guid.NewGuid().ToString("N"),
                                                     null,
                                                     1,
                                                     BridgeContract.TppaRecipient,
                                                     new BridgeCapabilitiesAnnouncePayload {
                                                         Controller = controller,
                                                         ControllerVersion = version
                                                     });
        }

        [Test]
        public async Task Hub_AnswersTheAnnounceUnderTheAnnouncedAddress() {
            var broker = new FakeMessageBroker();
            var hub = BridgeHub.EnsureInitialized(broker);
            try {
                hub.ControllerAddress.Should().Be(BridgeContract.ControllerRecipient);

                await hub.OnMessageReceived(new ControllerMessage(Announce("AcmePolar", "9.9.9.9"), Guid.NewGuid()));

                hub.ControllerAddress.Should().Be("AcmePolar");
                broker.Last(BridgeKind.Capabilities).IntendedRecipient.Should().Be("AcmePolar");
            } finally {
                BridgeHub.Shutdown();
            }
        }

        [Test]
        public async Task Hub_KeepsTheRunOwnerWhenAnotherControllerAnnounces() {
            var broker = new FakeMessageBroker();
            var hub = BridgeHub.EnsureInitialized(broker);
            var owner = Guid.NewGuid();
            try {
                await hub.OnMessageReceived(new ControllerMessage(Announce("AcmePolar"), owner));

                using var run = new BridgeControllerRun(broker, new CancellationTokenSource());
                await run.OpenAsync(1.0, false, CancellationToken.None);
                hub.AttachSession(run, owner);

                // A second controller announcing itself must not take the running session over.
                await hub.OnMessageReceived(new ControllerMessage(Announce("RivalPolar"), Guid.NewGuid()));

                hub.ControllerAddress.Should().Be("AcmePolar");
                run.ControllerAddress.Should().Be("AcmePolar");
                run.OwnerId.Should().Be(owner);
            } finally {
                BridgeHub.Shutdown();
            }
        }

        [Test]
        public async Task Hub_IgnoresMessagesFromAControllerThatDoesNotOwnTheRun() {
            var broker = new FakeMessageBroker();
            var hub = BridgeHub.EnsureInitialized(broker);
            var owner = Guid.NewGuid();
            try {
                await hub.OnMessageReceived(new ControllerMessage(Announce("AcmePolar"), owner));

                using var run = new BridgeControllerRun(broker, new CancellationTokenSource(), "session-1");
                await run.OpenAsync(1.0, false, CancellationToken.None);
                hub.AttachSession(run, owner);

                var readiness = Command(BridgeKind.ControllerReady,
                                        payload: new BridgeControllerReadyPayload { HardwareReady = true });

                await hub.OnMessageReceived(new ControllerMessage(readiness, Guid.NewGuid()));
                run.ControllerHardwareReady.Should().BeFalse();

                // The owner's own report still reaches the run.
                await hub.OnMessageReceived(new ControllerMessage(
                    Command(BridgeKind.ControllerReady, payload: new BridgeControllerReadyPayload { HardwareReady = true }),
                    owner));
                run.ControllerHardwareReady.Should().BeTrue();
            } finally {
                BridgeHub.Shutdown();
            }
        }

        [Test]
        public async Task Hub_ListsEveryRegisteredControllerForTheSystemSelection() {
            var broker = new FakeMessageBroker();
            var hub = BridgeHub.EnsureInitialized(broker);
            var selected = Guid.NewGuid();
            try {
                await hub.OnMessageReceived(new ControllerMessage(Announce("AcmePolar"), selected));
                await hub.OnMessageReceived(new ControllerMessage(Announce("RivalPolar"), Guid.NewGuid()));

                hub.RegisteredControllers.Should().HaveCount(2);
                hub.IsRegistered(selected).Should().BeTrue();
                hub.IsRegistered(Guid.NewGuid()).Should().BeFalse();

                using var run = new BridgeControllerRun(broker, new CancellationTokenSource());
                await run.OpenAsync(1.0, false, CancellationToken.None);
                hub.AttachSession(run, selected);

                // The run works with the controller the operator selected, while both stay in the list.
                hub.SessionOwnerId.Should().Be(selected);
                hub.ControllerAddress.Should().Be("AcmePolar");
                run.OwnerId.Should().Be(selected);
                hub.RegisteredControllers.Should().HaveCount(2);
            } finally {
                BridgeHub.Shutdown();
            }
        }

        [Test]
        public async Task Hub_DropsAControllerThatSwitchesItsBrokerOff() {
            var broker = new FakeMessageBroker();
            var hub = BridgeHub.EnsureInitialized(broker);
            var controller = Guid.NewGuid();
            try {
                await hub.OnMessageReceived(new ControllerMessage(Announce("AcmePolar"), controller));
                hub.IsRegistered(controller).Should().BeTrue();

                // Switching the broker off says goodbye, so the list does not have to wait for the presence window.
                await hub.OnMessageReceived(new ControllerMessage(
                    Command(BridgeKind.Cancel, payload: new BridgeCancelPayload { Reason = BridgeReason.BrokerDisabled }),
                    controller));

                hub.IsRegistered(controller).Should().BeFalse();
                hub.RegisteredControllers.Should().BeEmpty();
            } finally {
                BridgeHub.Shutdown();
            }
        }

        [Test]
        public void EnvelopeRoundTripsThroughJson() {
            var envelope = BridgeEnvelope.Create(BridgeKind.Measurement,
                                                            "session-1",
                                                            "command-1",
                                                            null,
                                                            7,
                                                            BridgeContract.ControllerRecipient,
                                                            new BridgeMeasurementPayload { TotalErrorArcMin = 3.25 });

            var restored = BridgeEnvelope.FromJson(envelope.ToJson());

            restored.Kind.Should().Be(BridgeKind.Measurement);
            restored.SessionId.Should().Be("session-1");
            restored.SequenceNumber.Should().Be(7);
            restored.PayloadAs<BridgeMeasurementPayload>().TotalErrorArcMin.Should().Be(3.25);
        }
    }
}
