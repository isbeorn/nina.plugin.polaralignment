using FluentAssertions;
using NINA.Core.Model;
using NINA.Plugin.Interfaces;
using NINA.Plugins.PolarAlignment.External;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Protocol level tests of the external correction session: capture windows, silence watchdog,
    /// heartbeat driven keep-alive, idempotency and the session ending rules. The camera and mount
    /// loop lives in the sequence item, so it is not exercised here.
    /// </summary>
    [TestFixture]
    public class ExternalCorrectionProtocolTest {

        private sealed class FakeMessageBroker : IMessageBroker {
            private readonly object gate = new object();
            private readonly List<ExternalCorrectionEnvelope> published = new List<ExternalCorrectionEnvelope>();

            public IReadOnlyList<ExternalCorrectionEnvelope> Published {
                get { lock (gate) { return published.ToList(); } }
            }

            public ExternalCorrectionEnvelope Last(string kind) {
                lock (gate) {
                    return published.LastOrDefault(e => e.Kind == kind);
                }
            }

            public int Count(string kind) {
                lock (gate) {
                    return published.Count(e => e.Kind == kind);
                }
            }

            public void Clear() {
                lock (gate) { published.Clear(); }
            }

            public Task Publish(IMessage message) {
                var envelope = ExternalCorrectionEnvelope.FromJson(message.Content as string);
                lock (gate) { published.Add(envelope); }
                return Task.CompletedTask;
            }

            public void Subscribe(string topic, ISubscriber subscriber) { }

            public void Unsubscribe(string topic, ISubscriber subscriber) { }
        }

        private static ExternalCorrectionOptions FastOptions(int? silenceTimeoutMs = null, int? graceMs = null, int sessionTimeoutSec = 3600) {
            return new ExternalCorrectionOptions {
                HeartbeatMs = 100000,
                SilenceTimeoutMs = silenceTimeoutMs ?? 400,
                ReadyTimeoutMs = 500,
                GraceAfterSilenceMs = graceMs ?? 200,
                SessionTimeoutSec = sessionTimeoutSec,
                StopAckTimeoutMs = 400,
                ToleranceArcMin = 1.0
            };
        }

        private static ExternalCorrectionSession NewSession(FakeMessageBroker broker, ExternalCorrectionOptions options = null) {
            var session = new ExternalCorrectionSession(broker, "session-1");
            session.ApplyOptions(options ?? FastOptions());
            return session;
        }

        private static ExternalCorrectionEnvelope Command(string kind,
                                                          string sessionId = "session-1",
                                                          object payload = null,
                                                          string commandId = null) {
            return ExternalCorrectionEnvelope.Create(kind,
                                                     sessionId,
                                                     commandId ?? Guid.NewGuid().ToString("N"),
                                                     null,
                                                     1,
                                                     ExternalCorrectionContract.TppaRecipient,
                                                     payload);
        }

        private static ExternalMeasurementPayload Measurement() {
            return new ExternalMeasurementPayload {
                MeasurementId = Guid.NewGuid().ToString("N"),
                Status = ExternalMeasurementStatus.Valid,
                AzimuthErrorArcMin = 5,
                AltitudeErrorArcMin = 3,
                TotalErrorArcMin = 5.83,
                ToleranceArcMin = 1,
                ToleranceReached = false,
                Northern = true,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }

        [Test]
        public async Task Open_PublishesPreparingSessionState() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);

            await session.OpenAsync(1.0, false, CancellationToken.None);

            broker.Last(ExternalCorrectionKind.SessionState).PayloadAs<ExternalSessionStatePayload>().State
                .Should().Be(ExternalCorrectionState.Preparing);
        }

        [Test]
        public async Task WaitForControllerReady_ReturnsTrueOnControllerReady() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.ControllerReady));

            (await session.WaitForControllerReadyAsync(CancellationToken.None)).Should().BeTrue();
        }

        [Test]
        public async Task WaitForControllerReady_ReturnsFalseWhenNothingAnswers() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            (await session.WaitForControllerReadyAsync(CancellationToken.None)).Should().BeFalse();
        }

        [Test]
        public async Task BeginAdjustment_GrantsWindowAndAnswersAdjustmentGranted() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.BeginAdjustment,
                                           payload: new ExternalAdjustmentRequestPayload { MeasurementId = "m1" }));
            var request = await session.WaitForControllerRequestAsync(CancellationToken.None);

            request.Kind.Should().Be(ExternalControllerRequestKind.AdjustWindow);
            var windowId = await session.GrantWindowAsync(request, CancellationToken.None);

            windowId.Should().NotBeNullOrEmpty();
            session.IsWindowOpen.Should().BeTrue();
            broker.Last(ExternalCorrectionKind.AdjustmentGranted).PayloadAs<ExternalAdjustmentGrantPayload>()
                .WindowId.Should().Be(windowId);
        }

        [Test]
        public async Task RepeatedBeginAdjustment_ReusesTheSameWindow() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            var begin = Command(ExternalCorrectionKind.BeginAdjustment,
                                payload: new ExternalAdjustmentRequestPayload { MeasurementId = "m1" });
            session.HandleEnvelope(begin);
            var firstRequest = await session.WaitForControllerRequestAsync(CancellationToken.None);
            var firstWindowId = await session.GrantWindowAsync(firstRequest, CancellationToken.None);

            // Repeating the same command must not open a second window: TPPA just repeats the grant.
            session.HandleEnvelope(begin);
            await Task.Delay(50);

            broker.Count(ExternalCorrectionKind.AdjustmentGranted).Should().Be(2);
            broker.Last(ExternalCorrectionKind.AdjustmentGranted).PayloadAs<ExternalAdjustmentGrantPayload>()
                .WindowId.Should().Be(firstWindowId);
            session.CurrentWindowId.Should().Be(firstWindowId);
        }

        [Test]
        public async Task RepeatedRequestMeasurement_IsFlaggedAsDuplicate() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            var payload = new ExternalMeasurementRequestPayload { StationaryAndSettled = true };
            var commandId = Guid.NewGuid().ToString("N");
            session.HandleEnvelope(Command(ExternalCorrectionKind.RequestMeasurement, payload: payload, commandId: commandId));
            session.HandleEnvelope(Command(ExternalCorrectionKind.RequestMeasurement, payload: payload, commandId: commandId));

            var first = await session.WaitForControllerRequestAsync(CancellationToken.None);
            var second = await session.WaitForControllerRequestAsync(CancellationToken.None);

            first.Duplicate.Should().BeFalse();
            second.Duplicate.Should().BeTrue();
        }

        [Test]
        public async Task RequestMeasurement_ClosesTheCaptureWindow() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.BeginAdjustment,
                                           payload: new ExternalAdjustmentRequestPayload { MeasurementId = "m1" }));
            await session.GrantWindowAsync(await session.WaitForControllerRequestAsync(CancellationToken.None), CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.RequestMeasurement,
                                           payload: new ExternalMeasurementRequestPayload { StationaryAndSettled = true }));
            var request = await session.WaitForControllerRequestAsync(CancellationToken.None);

            request.Kind.Should().Be(ExternalControllerRequestKind.RequestMeasurement);
            session.IsWindowOpen.Should().BeFalse();
        }

        [Test]
        public async Task SilentController_LosesTheWindowButKeepsTheSession() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker, FastOptions(silenceTimeoutMs: 150));
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.BeginAdjustment,
                                           payload: new ExternalAdjustmentRequestPayload { MeasurementId = "m1" }));
            await session.GrantWindowAsync(await session.WaitForControllerRequestAsync(CancellationToken.None), CancellationToken.None);

            var expired = await session.WaitForControllerRequestAsync(CancellationToken.None);

            expired.Kind.Should().Be(ExternalControllerRequestKind.WindowExpired);
            session.IsWindowOpen.Should().BeFalse();
            session.IsActive.Should().BeTrue();
            broker.Last(ExternalCorrectionKind.SessionState).PayloadAs<ExternalSessionStatePayload>().Reason
                .Should().Be(ExternalCorrectionReason.SilenceTimeout);
        }

        [Test]
        public async Task LostController_EndsTheSessionAfterTheGracePeriod() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker, FastOptions(silenceTimeoutMs: 100, graceMs: 100));
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.BeginAdjustment,
                                           payload: new ExternalAdjustmentRequestPayload { MeasurementId = "m1" }));
            await session.GrantWindowAsync(await session.WaitForControllerRequestAsync(CancellationToken.None), CancellationToken.None);

            (await session.WaitForControllerRequestAsync(CancellationToken.None)).Kind
                .Should().Be(ExternalControllerRequestKind.WindowExpired);
            (await session.WaitForControllerRequestAsync(CancellationToken.None)).Kind
                .Should().Be(ExternalControllerRequestKind.ExternalLost);
        }

        [Test]
        public async Task KeepAlive_KeepsALongMoveWindowOpen() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker, FastOptions(silenceTimeoutMs: 150));
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.BeginAdjustment,
                                           payload: new ExternalAdjustmentRequestPayload { MeasurementId = "m1" }));
            var grantedWindowId = await session.GrantWindowAsync(await session.WaitForControllerRequestAsync(CancellationToken.None), CancellationToken.None);

            // The controller keeps the window alive for longer than three silence timeouts, which is
            // what a long ALIGN chain does (verified on a 90 s move in the plan).
            var keepAliveJob = Task.Run(async () => {
                for (var i = 0; i < 10; i++) {
                    await Task.Delay(40);
                    session.HandleEnvelope(Command(ExternalCorrectionKind.KeepAlive,
                                                   payload: new ExternalKeepAlivePayload { WindowId = grantedWindowId }));
                }
            });

            await keepAliveJob;
            session.HandleEnvelope(Command(ExternalCorrectionKind.RequestMeasurement,
                                           payload: new ExternalMeasurementRequestPayload { WindowId = grantedWindowId, StationaryAndSettled = true }));
            var request = await session.WaitForControllerRequestAsync(CancellationToken.None);

            request.Kind.Should().Be(ExternalControllerRequestKind.RequestMeasurement);
            request.WindowId.Should().Be(grantedWindowId);
        }

        [Test]
        public async Task RequestCompletion_IsDeliveredEvenWhileTheWindowIsOpen() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.BeginAdjustment,
                                           payload: new ExternalAdjustmentRequestPayload { MeasurementId = "m1" }));
            await session.GrantWindowAsync(await session.WaitForControllerRequestAsync(CancellationToken.None), CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.RequestCompletion,
                                           payload: new ExternalCompletionRequestPayload { WindowId = session.CurrentWindowId }));
            var request = await session.WaitForControllerRequestAsync(CancellationToken.None);

            request.Kind.Should().Be(ExternalControllerRequestKind.RequestCompletion);
        }

        [Test]
        public async Task Cancel_IsDeliveredAsCancelRequest() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.Cancel,
                                           payload: new ExternalCancelPayload { Reason = ExternalCorrectionReason.ControllerCancel }));
            var request = await session.WaitForControllerRequestAsync(CancellationToken.None);

            request.Kind.Should().Be(ExternalControllerRequestKind.Cancel);
            request.Cancel.Reason.Should().Be(ExternalCorrectionReason.ControllerCancel);
        }

        [Test]
        public async Task Fault_IsDeliveredAsCancelCarryingTheHardwareStopStatus() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.Fault,
                                           payload: new ExternalFaultPayload {
                                               Reason = ExternalCorrectionReason.ControllerFault,
                                               Detail = "serial link lost",
                                               HardwareStopStatus = ExternalHardwareStopStatus.Unknown
                                           }));
            var request = await session.WaitForControllerRequestAsync(CancellationToken.None);

            request.Kind.Should().Be(ExternalControllerRequestKind.Cancel);
            request.Cancel.Reason.Should().Be(ExternalCorrectionReason.ControllerFault);
        }

        [Test]
        public async Task RequestStop_ReturnsTheAcknowledgedHardwareStatus() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            var stopTask = session.RequestStopAsync(ExternalCorrectionReason.UserStop, CancellationToken.None);
            session.HandleEnvelope(Command(ExternalCorrectionKind.Stopped,
                                           payload: new ExternalStoppedPayload {
                                               Reason = ExternalCorrectionReason.UserStop,
                                               HardwareStopStatus = ExternalHardwareStopStatus.Ok
                                           }));

            var status = await stopTask;

            status.Should().Be(ExternalHardwareStopStatus.Ok);
            broker.Last(ExternalCorrectionKind.StopRequested).PayloadAs<ExternalStopRequestPayload>().Reason
                .Should().Be(ExternalCorrectionReason.UserStop);
        }

        [Test]
        public async Task RequestStop_ReportsUnknownWhenTheControllerStaysSilent() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            var status = await session.RequestStopAsync(ExternalCorrectionReason.UserStop, CancellationToken.None);

            status.Should().Be(ExternalHardwareStopStatus.Unknown);
        }

        [Test]
        public async Task SessionTimeLimit_IsReportedAndIgnoresWindowTime() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker, FastOptions(sessionTimeoutSec: 0));
            await session.OpenAsync(1.0, false, CancellationToken.None);

            (await session.WaitForControllerRequestAsync(CancellationToken.None)).Kind
                .Should().Be(ExternalControllerRequestKind.SessionTimeout);
        }

        [Test]
        public async Task Measurement_IsAnnotatedWithSessionAndSampleIndex() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(2.5, false, CancellationToken.None);

            await session.PublishMeasurementAsync(Measurement(), CancellationToken.None);
            await session.PublishMeasurementAsync(Measurement(), CancellationToken.None);

            var published = broker.Published.Where(e => e.Kind == ExternalCorrectionKind.Measurement)
                                  .Select(e => e.PayloadAs<ExternalMeasurementPayload>())
                                  .ToList();

            published.Should().HaveCount(2);
            published[0].SessionId.Should().Be("session-1");
            published[0].SampleIndex.Should().Be(1);
            published[1].SampleIndex.Should().Be(2);
            published[1].ToleranceArcMin.Should().Be(2.5);
        }

        [Test]
        public async Task EndSession_PublishesSessionEndedWithTheToleranceUsed() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.5, false, CancellationToken.None);
            await session.PublishMeasurementAsync(Measurement(), CancellationToken.None);

            await session.EndAsync(ExternalCorrectionReason.Completed,
                                   true,
                                   new ExternalSessionEndedPayload {
                                       AzimuthErrorArcMin = 0.4,
                                       AltitudeErrorArcMin = 0.3,
                                       TotalErrorArcMin = 0.5
                                   },
                                   CancellationToken.None);

            var ended = broker.Last(ExternalCorrectionKind.SessionEnded).PayloadAs<ExternalSessionEndedPayload>();
            ended.Achieved.Should().BeTrue();
            ended.Reason.Should().Be(ExternalCorrectionReason.Completed);
            ended.ToleranceUsedArcMin.Should().Be(1.5);
            ended.SamplesUsed.Should().Be(1);
            session.IsActive.Should().BeFalse();
        }

        [Test]
        public async Task MessageForAnotherSession_IsIgnored() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            session.HandleEnvelope(Command(ExternalCorrectionKind.RequestMeasurement,
                                           sessionId: "some-other-session",
                                           payload: new ExternalMeasurementRequestPayload { StationaryAndSettled = true }));

            session.HandleEnvelope(Command(ExternalCorrectionKind.ControllerReady));
            (await session.WaitForControllerRequestAsync(CancellationToken.None)).Kind
                .Should().Be(ExternalControllerRequestKind.ControllerReady);
        }

        [Test]
        public async Task MessageWithAnotherInterfaceVersion_IsIgnored() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            var envelope = Command(ExternalCorrectionKind.ControllerReady);
            envelope.Version = ExternalCorrectionContract.InterfaceVersion + 1;
            session.HandleEnvelope(envelope);

            session.HandleEnvelope(Command(ExternalCorrectionKind.ControllerReady));
            (await session.WaitForControllerRequestAsync(CancellationToken.None)).Kind
                .Should().Be(ExternalControllerRequestKind.ControllerReady);
        }

        [Test]
        public async Task Cancellation_UnblocksTheWaitingLoop() {
            var broker = new FakeMessageBroker();
            using var session = NewSession(broker);
            await session.OpenAsync(1.0, false, CancellationToken.None);

            using var cts = new CancellationTokenSource();
            var wait = session.WaitForControllerRequestAsync(cts.Token);
            cts.Cancel();

            var cancelled = false;
            try {
                await wait;
            } catch (OperationCanceledException) {
                cancelled = true;
            }

            cancelled.Should().BeTrue();
        }

        [Test]
        public void EnvelopeRoundTripsThroughJson() {
            var envelope = ExternalCorrectionEnvelope.Create(ExternalCorrectionKind.Measurement,
                                                            "session-1",
                                                            "command-1",
                                                            null,
                                                            7,
                                                            ExternalCorrectionContract.ControllerRecipient,
                                                            new ExternalMeasurementPayload { TotalErrorArcMin = 3.25 });

            var restored = ExternalCorrectionEnvelope.FromJson(envelope.ToJson());

            restored.Kind.Should().Be(ExternalCorrectionKind.Measurement);
            restored.SessionId.Should().Be("session-1");
            restored.SequenceNumber.Should().Be(7);
            restored.PayloadAs<ExternalMeasurementPayload>().TotalErrorArcMin.Should().Be(3.25);
        }
    }
}
