using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>Motion boundary for the calibration: relative axis moves in axis arcminutes.</summary>
    public interface IOapaCalibrationMotion {
        Task MoveRelative(Axis axis, float arcmin, CancellationToken token);

        /// <summary>
        /// Where the axis is now, in the same arcminutes <see cref="MoveRelative"/> speaks, or
        /// null when this motion source cannot say.
        ///
        /// Read once before the pass starts, it is the only quantity that survives a failure
        /// with the sky unavailable: the commanded sum does not, because play makes the sum
        /// and the physical position two different things, and the sky does not, because the
        /// failure being recovered from is often the sky itself.
        ///
        /// The default answers "I do not know", which is the honest answer for a driver
        /// without absolute positioning and the one that keeps the calibration from driving
        /// anything blind.
        /// </summary>
        Task<float?> ReadPosition(Axis axis, CancellationToken token) => Task.FromResult<float?>(null);

        /// <summary>
        /// Drives the axis to a position previously returned by <see cref="ReadPosition"/>.
        /// Only ever called with such a position, so a source that cannot report one is never
        /// asked to reach one.
        /// </summary>
        Task MoveAbsolute(Axis axis, float position, CancellationToken token)
            => throw new NotSupportedException("This motion source cannot position absolutely.");
    }
}
