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

        /// <summary>
        /// Whether the automated controller may use the aggressive correction profile
        /// (error-scaled probes, 75% correction candidate) with this system. Systems
        /// without a specific policy keep the legacy conservative profile.
        /// </summary>
        bool AggressiveCorrectionProfile { get; }

        Task Connect();
        void Disconnect();
        Task<bool> TryNudgeX(float position, CancellationToken token);
        Task<bool> TryNudgeY(float position, CancellationToken token);

        /// <summary>
        /// Relative nudge issued by the automated fine-approach loop. Systems without a
        /// specific policy behave exactly like <see cref="TryNudgeX"/>; systems that model
        /// large backlash (OAPA) may skip the clearing excursion for sub-compensation
        /// corrections, where clearing would inject more error than the nudge removes.
        /// </summary>
        Task<bool> TryFineNudgeX(float position, CancellationToken token);

        /// <summary>Altitude counterpart of <see cref="TryFineNudgeX"/>.</summary>
        Task<bool> TryFineNudgeY(float position, CancellationToken token);
        Task NudgeX(float position, CancellationToken token);
        Task NudgeY(float position, CancellationToken token);
        void RaiseAllPropertiesChanged();
    }
}
