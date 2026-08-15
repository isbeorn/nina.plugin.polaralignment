using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>Capture-and-solve boundary for the OAPA self-calibration.</summary>
    public interface IOapaCalibrationSolver {
        /// <summary>Captures an image and plate-solves it, retrying internally as configured.</summary>
        Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token);

        /// <summary>
        /// Marks the start of a calibration pass so the solver can freeze its topocentric
        /// reference epoch. A tracked field keeps its RA/Dec while its Alt/Az drifts with
        /// sidereal time, so samples transformed each at their own wall-clock time alias
        /// sky rotation into platform motion — the displacement comparisons only mean
        /// "axis motion" when every sample of a pass is expressed at one common epoch.
        /// Default is a no-op for solvers that are time-invariant already (test fakes).
        /// </summary>
        void BeginCalibration() { }
    }
}
