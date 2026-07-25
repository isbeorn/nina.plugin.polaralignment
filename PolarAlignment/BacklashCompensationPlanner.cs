using System;

namespace NINA.Plugins.PolarAlignment {
    internal static class BacklashCompensationPlanner {
        public static (float FirstMove, float SecondMove) CreateSequence(float compensation, LastDirection targetDirection) {
            var directionSign = targetDirection == LastDirection.Positive ? 1f : -1f;
            return (-directionSign * compensation, directionSign * compensation);
        }

        /// <summary>
        /// Decides whether a direction change warrants a backlash-clearing excursion.
        /// Clearing is skipped when the commanded move is smaller than the compensation
        /// itself: with a large compensation (several arcminutes) and sub-arcminute
        /// correction nudges, the out-and-back excursion injects more error than the
        /// nudge removes. Callers that always want clearing pass float.MaxValue.
        /// </summary>
        public static bool ShouldClear(float movedMagnitude, float compensation, LastDirection lastDirection, LastDirection currentDirection) {
            if (lastDirection == currentDirection) { return false; }
            if (Math.Abs(compensation) <= 0f) { return false; }
            return Math.Abs(movedMagnitude) >= Math.Abs(compensation);
        }
    }
}
