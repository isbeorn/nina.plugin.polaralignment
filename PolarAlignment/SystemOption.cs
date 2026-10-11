using System;

namespace NINA.Plugins.PolarAlignment {

    /// <summary>
    /// One entry of the alignment system selection: a built-in system (UPAS, OAPA) or a controller that announced
    /// itself on the NINA message broker. A controller is identified by its broker identity, because the name it
    /// announces is only the label the operator reads and two controllers may use the same one.
    /// </summary>
    public sealed class SystemOption {
        public SystemOption(PolarAlignmentSystemType type, string displayName, Guid? controllerId) {
            Type = type;
            DisplayName = displayName;
            ControllerId = controllerId;
        }

        public PolarAlignmentSystemType Type { get; }

        /// <summary>What the combo box shows: "None", "UPAS", "OAPA" or "External: MLAstroRPA 3.1.3.0".</summary>
        public string DisplayName { get; }

        /// <summary>Broker identity of the controller, null for the built-in systems.</summary>
        public Guid? ControllerId { get; }

        public override string ToString() => DisplayName;
    }
}
