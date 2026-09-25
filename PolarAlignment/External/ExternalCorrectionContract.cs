using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Core.Model;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;

namespace NINA.Plugins.PolarAlignment.External {

    /// <summary>
    /// Names and default values shared by TPPA and an external alignment controller (MLAstroRPA).
    /// Both sides keep their own copy of these names: the plugins do not share an assembly.
    /// Changes here are a contract change and must be mirrored on the controller side.
    /// </summary>
    public static class ExternalCorrectionContract {

        /// <summary>Interface version carried by every message. Bump on an incompatible change.</summary>
        public const int InterfaceVersion = 1;

        /// <summary>Topic carrying messages published by TPPA (controller subscribes).</summary>
        public const string EventTopic = "PolarAlignmentPlugin_PolarAlignment_ExternalEvent";

        /// <summary>Topic carrying messages published by the controller (TPPA subscribes).</summary>
        public const string CommandTopic = "PolarAlignmentPlugin_PolarAlignment_ExternalCommand";

        /// <summary>Value used in <c>IntendedRecipient</c> for TPPA addressed messages.</summary>
        public const string TppaRecipient = "TPPA";

        /// <summary>Value used in <c>IntendedRecipient</c> for controller addressed messages.</summary>
        public const string ControllerRecipient = "MLAstroRPA";
    }

    /// <summary>
    /// Message kinds. TPPA publishes <see cref="Capabilities"/>, <see cref="Measurement"/>,
    /// <see cref="AdjustmentGranted"/>, <see cref="SessionState"/>, <see cref="PauseRequested"/>,
    /// <see cref="SessionEnded"/> and <see cref="StopRequested"/>; the controller publishes the
    /// remaining kinds.
    /// </summary>
    public static class ExternalCorrectionKind {
        public const string Capabilities = "Capabilities";
        public const string ControllerReady = "ControllerReady";
        public const string Measurement = "Measurement";
        public const string BeginAdjustment = "BeginAdjustment";
        public const string AdjustmentGranted = "AdjustmentGranted";
        public const string RequestMeasurement = "RequestMeasurement";
        public const string RequestCompletion = "RequestCompletion";
        public const string KeepAlive = "KeepAlive";
        public const string PauseRequested = "PauseRequested";
        public const string SessionState = "SessionState";
        public const string StopRequested = "StopRequested";
        public const string Stopped = "Stopped";
        public const string Cancel = "Cancel";
        public const string Fault = "Fault";
        public const string SessionEnded = "SessionEnded";

        public static readonly string[] All = {
            Capabilities, ControllerReady, Measurement, BeginAdjustment, AdjustmentGranted,
            RequestMeasurement, RequestCompletion, KeepAlive, PauseRequested, SessionState,
            StopRequested, Stopped, Cancel, Fault, SessionEnded
        };
    }

    /// <summary>Reasons attached to <c>SessionState</c>, <c>StopRequested</c>, <c>Cancel</c> and <c>SessionEnded</c>.</summary>
    public static class ExternalCorrectionReason {
        public const string UserStop = "UserStop";
        public const string SequenceCancel = "SequenceCancel";
        public const string WindowClosed = "WindowClosed";
        public const string Disconnect = "Disconnect";
        public const string SilenceTimeout = "SilenceTimeout";
        public const string SessionTimeout = "SessionTimeout";
        public const string StopAckTimeout = "StopAckTimeout";
        public const string ExternalLost = "ExternalLost";
        public const string NoControllerReady = "NoControllerReady";
        public const string ControllerFault = "ControllerFault";
        public const string ControllerCancel = "ControllerCancel";
        public const string CaptureFailed = "CaptureFailed";
        public const string Superseded = "Superseded";
        public const string MeasurementsFinished = "MeasurementsFinished";
        public const string ReferenceSweep = "ReferenceSweep";
        public const string Completed = "Completed";
        public const string StepFinished = "StepFinished";
        public const string VerifyOnly = "VerifyOnly";
        public const string CompletionRequested = "CompletionRequested";
        public const string NotAchieved = "NotAchieved";

        /// <summary>Operator paused the run: the controller has to stop a move in progress.</summary>
        public const string Paused = "Paused";

        /// <summary>Operator resumed the run: the controller may move and measure again.</summary>
        public const string Resumed = "Resumed";
    }

    /// <summary>Session states reported through <c>SessionState</c>.</summary>
    public static class ExternalCorrectionState {
        public const string Preparing = "Preparing";
        public const string Measuring = "Measuring";
        public const string WaitingForRequest = "WaitingForRequest";
        public const string WindowOpen = "WindowOpen";
        public const string Verifying = "Verifying";
        public const string Ended = "Ended";
    }

    /// <summary>Status of a published <c>Measurement</c>.</summary>
    public static class ExternalMeasurementStatus {
        public const string Valid = "Valid";
        public const string Unstable = "Unstable";
        public const string CaptureFailed = "CaptureFailed";
    }

    /// <summary>Hardware stop outcome reported back with <c>Stopped</c> / <c>Fault</c>.</summary>
    public static class ExternalHardwareStopStatus {
        public const string Ok = "ok";
        public const string Unknown = "unknown";
        public const string Fault = "fault";
    }

    /// <summary>Kind of the next controller-side event a waiting TPPA loop has to act on.</summary>
    public enum ExternalControllerRequestKind {
        ControllerReady,
        AdjustWindow,
        RequestMeasurement,
        RequestCompletion,
        Cancel,
        WindowExpired,
        ExternalLost,
        SessionTimeout,
        StopAcknowledged
    }

    /// <summary>
    /// Wire envelope. The JSON of this object is the <see cref="IMessage.Content"/> of every message
    /// on both external topics, so the two plugins never have to share a CLR type.
    /// </summary>
    public sealed class ExternalCorrectionEnvelope {
        public int Version { get; set; } = ExternalCorrectionContract.InterfaceVersion;
        public string SessionId { get; set; }
        public string CommandId { get; set; }
        public string ReplyTo { get; set; }
        public long SequenceNumber { get; set; }
        public string Kind { get; set; }
        public string IntendedRecipient { get; set; }
        public JObject Payload { get; set; }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.None);

        public static ExternalCorrectionEnvelope FromJson(string json) {
            if (string.IsNullOrWhiteSpace(json)) { return null; }
            try {
                return JsonConvert.DeserializeObject<ExternalCorrectionEnvelope>(json);
            } catch (JsonException) {
                return null;
            }
        }

        public T PayloadAs<T>() where T : class {
            return Payload?.ToObject<T>();
        }

        public static ExternalCorrectionEnvelope Create(string kind,
                                                       string sessionId,
                                                       string commandId,
                                                       string replyTo,
                                                       long sequenceNumber,
                                                       string recipient,
                                                       object payload) {
            return new ExternalCorrectionEnvelope {
                Kind = kind,
                SessionId = sessionId,
                CommandId = commandId,
                ReplyTo = replyTo,
                SequenceNumber = sequenceNumber,
                IntendedRecipient = recipient,
                Payload = payload == null ? null : JObject.FromObject(payload)
            };
        }
    }

    /// <summary>Broker message published on the TPPA -&gt; controller topic.</summary>
    public sealed class ExternalCorrectionEventMessage : IMessage {
        private readonly string json;

        public ExternalCorrectionEventMessage(ExternalCorrectionEnvelope envelope) {
            json = envelope.ToJson();
        }

        public Guid SenderId => ResolvePluginId();
        public string Sender => nameof(PolarAlignmentPlugin);
        public DateTimeOffset SentAt => DateTime.UtcNow;
        public Guid MessageId => Guid.NewGuid();
        public DateTimeOffset? Expiration => null;
        public Guid? CorrelationId => null;
        public int Version => ExternalCorrectionContract.InterfaceVersion;
        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();
        public string Topic => ExternalCorrectionContract.EventTopic;
        public object Content => json;

        internal static Guid ResolvePluginId() {
            return Guid.TryParse(PolarAlignmentPlugin.PluginId, out var id) ? id : Guid.Empty;
        }
    }

    /// <summary>Broker message published on the controller -&gt; TPPA topic.</summary>
    public sealed class ExternalCorrectionCommandMessage : IMessage {
        private readonly string json;

        public ExternalCorrectionCommandMessage(ExternalCorrectionEnvelope envelope) {
            json = envelope.ToJson();
        }

        public Guid SenderId => ExternalCorrectionEventMessage.ResolvePluginId();
        public string Sender => nameof(PolarAlignmentPlugin);
        public DateTimeOffset SentAt => DateTime.UtcNow;
        public Guid MessageId => Guid.NewGuid();
        public DateTimeOffset? Expiration => null;
        public Guid? CorrelationId => null;
        public int Version => ExternalCorrectionContract.InterfaceVersion;
        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();
        public string Topic => ExternalCorrectionContract.CommandTopic;
        public object Content => json;
    }
}
