namespace NINA.Plugins.PolarAlignment.Instructions {

    /// <summary>
    /// Gates the automatic pause issued when the correction controller halts on a
    /// detected runaway. The pause fires exactly once, on the first halted cycle:
    /// pausing makes the halt unmissable, while never re-firing lets the user resume
    /// into display-only/manual operation without being paused again on every cycle.
    /// </summary>
    public sealed class RunawayPauseGate {
        private bool issued;

        /// <summary>
        /// Registers the halt state of the current correction cycle and returns true
        /// when the sequence should pause now.
        /// </summary>
        public bool ShouldPause(bool automatedAdjustmentsHalted) {
            if (!automatedAdjustmentsHalted || issued) {
                return false;
            }
            issued = true;
            return true;
        }
    }
}
