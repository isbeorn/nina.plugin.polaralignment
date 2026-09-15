using System.Collections.Concurrent;

namespace NINA.Plugins.PolarAlignment {

    /// <summary>
    /// Which side of its own play each axis is currently engaged on, remembered for as long
    /// as the process lives rather than for as long as one system object does.
    ///
    /// The alignment instruction constructs a fresh <see cref="UniversalPolarAlignmentBase"/>
    /// on every Execute - a field log shows "Found OAPA System on COM11" four times in one
    /// session - while the panel holds another one and the mechanism underneath holds still.
    /// With the state on the object, every new run started out believing both axes were
    /// engaged positive, so the first correction of the run was planned as a reversal when it
    /// was not (injecting the whole play) or as a continuation when it was (losing it). On an
    /// axis with tens of arcminutes of backlash that is the largest single error of the run:
    /// one log shows a commanded -2.47' turning into a 43' swing this way.
    ///
    /// The mechanism is what remembers, so the state is keyed by system identity and axis and
    /// lives here. Two different controllers in one session (Avalon then OAPA) keep separate
    /// entries. A power cycle of the controller is not observable from here, but the
    /// consequence - one uncompensated reversal - is the same order as the pre-existing
    /// startup assumption and self-corrects on the next cycle.
    /// </summary>
    public static class AxisEngagementState {

        private static readonly ConcurrentDictionary<(string System, Axis Axis), LastDirection> state = new();

        public static LastDirection Get(string system, Axis axis)
            => state.TryGetValue((system, axis), out var d) ? d : LastDirection.Positive;

        public static void Set(string system, Axis axis, LastDirection direction)
            => state[(system, axis)] = direction;

        /// <summary>Drops all remembered state.</summary>
        public static void Reset() => state.Clear();
    }
}
