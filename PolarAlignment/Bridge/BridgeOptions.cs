namespace NINA.Plugins.PolarAlignment.Bridge {

    /// <summary>
    /// Tunables of the external correction protocol: TPPA publishes them in its <c>Capabilities</c> reply so the
    /// controller never has to hard code them, and the user needs no setting because the mode is detected
    /// automatically.
    /// </summary>
    public sealed class BridgeOptions {
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
        /// Safety limit of a single external session. It only counts the time the controller is NOT holding a
        /// capture window, so a long chain of moves can never trip it.
        /// </summary>
        public int SessionTimeoutSec { get; set; } = DefaultSessionTimeoutSec;

        public int StopAckTimeoutMs { get; set; } = DefaultStopAckTimeoutMs;
        public double ToleranceArcMin { get; set; }
        public bool ContinuousEstimation { get; set; }
    }
}
