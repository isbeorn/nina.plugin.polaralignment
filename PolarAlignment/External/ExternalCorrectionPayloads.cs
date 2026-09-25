using System;

namespace NINA.Plugins.PolarAlignment.External {

    /// <summary>
    /// Payload published by TPPA in reply to a controller <c>Capabilities</c> announcement. The
    /// controller must read every tunable value from here instead of hard coding it.
    /// </summary>
    public sealed class ExternalCapabilitiesPayload {
        public string Controller { get; set; }
        public string TppaVersion { get; set; }
        public int InterfaceVersion { get; set; }
        public string[] SupportedKinds { get; set; }
        public bool SessionActive { get; set; }
        public string ActiveSessionId { get; set; }
        public double ToleranceArcMin { get; set; }
        public bool AutoFinishConditionAvailable { get; set; }
        public int HeartbeatMs { get; set; }
        public int SilenceTimeoutMs { get; set; }
        public int ReadyTimeoutMs { get; set; }
        public int SessionTimeoutSec { get; set; }
        public int GraceAfterSilenceMs { get; set; }
        public int StopAckTimeoutMs { get; set; }
        public bool ContinuousEstimation { get; set; }
    }

    /// <summary>Payload sent by the controller to announce itself on the command topic.</summary>
    public sealed class ExternalCapabilitiesAnnouncePayload {
        public string Controller { get; set; }
        public string ControllerVersion { get; set; }
        public int InterfaceVersion { get; set; }
        public string[] SupportedKinds { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>Payload sent by the controller when its hardware is connected and its axes are idle.</summary>
    public sealed class ExternalControllerReadyPayload {
        public string Controller { get; set; }
        public string ControllerVersion { get; set; }
        public bool HardwareReady { get; set; }
        public string LinkPath { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// One polar-error sample. Values mirror exactly what TPPA feeds its own UI and correction loop:
    /// arcminutes are the unit TPPA works in, so the controller never has to convert or round. The
    /// correction direction is not sent: it follows from the sign of the error and, for altitude, the
    /// hemisphere flag.
    /// </summary>
    public sealed class ExternalMeasurementPayload {
        public string MeasurementId { get; set; }
        public string SessionId { get; set; }
        public string WindowId { get; set; }
        public int SampleIndex { get; set; }
        public bool IsFirstMeasurement { get; set; }
        public string Status { get; set; }
        public double AzimuthErrorArcMin { get; set; }
        public double AltitudeErrorArcMin { get; set; }
        public double TotalErrorArcMin { get; set; }
        public double ToleranceArcMin { get; set; }
        public bool ToleranceReached { get; set; }
        public bool AutoFinishConditionMet { get; set; }
        public int ConsecutiveBelowTolerance { get; set; }
        public bool Northern { get; set; }
        public bool ContinuousEstimation { get; set; }
        public DateTimeOffset TimestampUtc { get; set; }
    }

    /// <summary>Payload sent by the controller when it wants to hold the capture for a move sequence.</summary>
    public sealed class ExternalAdjustmentRequestPayload {
        public string MeasurementId { get; set; }
        public double? PlannedAzimuthArcMin { get; set; }
        public double? PlannedAltitudeArcMin { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Payload sent by TPPA when a capture window has been granted.</summary>
    public sealed class ExternalAdjustmentGrantPayload {
        public string WindowId { get; set; }
        public string MeasurementId { get; set; }
        public long MaxWindowMs { get; set; }
        public int SilenceTimeoutMs { get; set; }
        public DateTimeOffset GrantedAtUtc { get; set; }
    }

    /// <summary>Payload sent by the controller when it wants a new measurement.</summary>
    public sealed class ExternalMeasurementRequestPayload {
        public string WindowId { get; set; }
        public bool StationaryAndSettled { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Payload sent by the controller when it believes the alignment is finished.</summary>
    public sealed class ExternalCompletionRequestPayload {
        public string WindowId { get; set; }
        public string Reason { get; set; }
        public int ConsecutiveBelowTolerance { get; set; }
    }

    /// <summary>Payload sent by the controller to refresh the silence watchdog while it holds a window.</summary>
    public sealed class ExternalKeepAlivePayload {
        public string WindowId { get; set; }
        public string State { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// Payload of <c>PauseRequested</c>: TPPA tells the controller that the operator paused or resumed
    /// the run. While paused the controller must not start a move and has to stop a move in progress,
    /// because TPPA stops capturing and would leave the axes turning without a reader.
    /// </summary>
    public sealed class ExternalPauseRequestPayload {
        public bool Paused { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Payload sent by TPPA as a heartbeat and whenever the session state changes.</summary>
    public sealed class ExternalSessionStatePayload {
        public string State { get; set; }
        public string Reason { get; set; }
        public string WindowId { get; set; }
        public long WindowRemainingMs { get; set; }
        public int SamplesTaken { get; set; }
        public string LastMeasurementId { get; set; }
        public double ToleranceArcMin { get; set; }
        public long SessionElapsedMs { get; set; }
    }

    /// <summary>Payload sent by TPPA when it wants the controller to stop and park.</summary>
    public sealed class ExternalStopRequestPayload {
        public string Reason { get; set; }
        public int AckTimeoutMs { get; set; }
    }

    /// <summary>Payload sent by the controller as acknowledgement of a stop request.</summary>
    public sealed class ExternalStoppedPayload {
        public string Reason { get; set; }
        public string HardwareStopStatus { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>Payload sent by the controller when it cannot continue the session.</summary>
    public sealed class ExternalFaultPayload {
        public string Reason { get; set; }
        public string Detail { get; set; }
        public string HardwareStopStatus { get; set; }
    }

    /// <summary>Payload sent by the controller when it aborts the session on purpose.</summary>
    public sealed class ExternalCancelPayload {
        public string Reason { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Payload sent by TPPA as the last message of a session.</summary>
    public sealed class ExternalSessionEndedPayload {
        public string Reason { get; set; }
        public bool Achieved { get; set; }
        public double AzimuthErrorArcMin { get; set; }
        public double AltitudeErrorArcMin { get; set; }
        public double TotalErrorArcMin { get; set; }
        public double ToleranceUsedArcMin { get; set; }
        public int SamplesUsed { get; set; }
        public string HardwareStopStatus { get; set; }
        public string Detail { get; set; }
    }
}
