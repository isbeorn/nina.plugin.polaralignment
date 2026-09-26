using System;

namespace NINA.Plugins.PolarAlignment.Bridge {
    /// <summary>
    /// Reason and note of a controller cancel or fault, passed to
    /// <see cref="BridgeSession.ControllerStopRequested"/> as soon as the message arrives - before the
    /// request queue is served.
    /// </summary>
    public sealed class BridgeControllerStopEventArgs : EventArgs {
        /// <summary>Protocol reason, e.g. <see cref="BridgeReason.UserStop"/>.</summary>
        public string Reason { get; set; }

        /// <summary>Free text the controller attached to the cancel; may be empty.</summary>
        public string Note { get; set; }

        /// <summary>True when the message was a fault instead of a plain cancel.</summary>
        public bool IsFault { get; set; }
    }
}
