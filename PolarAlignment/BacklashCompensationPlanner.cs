namespace NINA.Plugins.PolarAlignment {
    internal static class BacklashCompensationPlanner {
        /// <summary>
        /// Moves that recover the play a direction-changing move just lost. The move that
        /// reversed into <paramref name="targetDirection"/> fell short by the backlash it
        /// crossed, so a single further move of the full compensation in that same
        /// direction closes the gap without introducing a new reversal. The previous
        /// zero-sum pair (−B then +B in the target direction) reversed twice and paid the
        /// play on both legs: under a dead-travel mechanism it moved the axis nowhere and
        /// the original shortfall survived it unchanged.
        /// </summary>
        public static float[] CreateSequence(float compensation, LastDirection targetDirection) {
            var directionSign = targetDirection == LastDirection.Positive ? 1f : -1f;
            return new[] { directionSign * compensation };
        }
    }
}
