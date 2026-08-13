using System;

namespace NINA.Plugins.PolarAlignment {
    /// <summary>
    /// Plans azimuth backlash compensation while maintaining a positive mechanical preload.
    /// </summary>
    internal static class BacklashCompensationPlanner {
        /// <summary>
        /// Returns an overtravel-and-return pair for negative movement, or no movement when the
        /// requested direction already matches the positive preload.
        /// </summary>
        public static (float FirstMove, float SecondMove) CreateSequence(float compensation, LastDirection targetDirection) {
            var magnitude = Math.Abs(compensation);
            if (magnitude == 0 || targetDirection == LastDirection.Positive) {
                return (0, 0);
            }

            return (-magnitude, magnitude);
        }
    }
}
