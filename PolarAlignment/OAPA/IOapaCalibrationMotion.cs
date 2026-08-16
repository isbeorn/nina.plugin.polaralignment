using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>Motion boundary for the calibration: relative axis moves in axis arcminutes.</summary>
    public interface IOapaCalibrationMotion {
        Task MoveRelative(Axis axis, float arcmin, CancellationToken token);
    }
}
