using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment {
    public interface IPolarAlignmentSystemVM {
        bool Connected { get; }
        bool DoAutomatedAdjustments { get; set; }
        double AutomatedAdjustmentSettleTime { get; set; }

        /// <summary>
        /// Maximum correction magnitude the automated controller may issue in a single
        /// cycle, given the currently measured total error in arcminutes. Systems that
        /// have no specific policy return the controller default.
        /// </summary>
        double GetMaximumCorrectionMagnitude(double currentTotalErrorArcmin);

        Task Connect();
        void Disconnect();
        Task<bool> TryNudgeX(float position, CancellationToken token);
        Task<bool> TryNudgeY(float position, CancellationToken token);
        Task NudgeX(float position, CancellationToken token);
        Task NudgeY(float position, CancellationToken token);
        void RaiseAllPropertiesChanged();
    }
}
