namespace NINA.Plugins.PolarAlignment.Instructions {

    /// <summary>
    /// Gates automatic completion of the polar alignment on consecutive confirmations
    /// below the tolerance, so a single lucky solve cannot end a non-converged procedure.
    /// </summary>
    public sealed class AutoFinishGate {
        private readonly int requiredConsecutive;

        public AutoFinishGate(int requiredConsecutive) {
            this.requiredConsecutive = requiredConsecutive;
        }

        /// <summary>
        /// Number of consecutive below-tolerance confirmations registered so far.
        /// Non-zero means a confirmation solve is pending and corrections should hold.
        /// </summary>
        public int Consecutive { get; private set; }

        public int RequiredConsecutive => requiredConsecutive;

        /// <summary>
        /// Registers a stable measurement and returns true when enough consecutive
        /// below-tolerance measurements have been observed to finish the alignment.
        /// </summary>
        public bool Register(bool belowTolerance) {
            Consecutive = belowTolerance ? Consecutive + 1 : 0;
            return Consecutive >= requiredConsecutive;
        }
    }
}
