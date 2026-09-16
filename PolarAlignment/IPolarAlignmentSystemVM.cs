using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment {
    public interface IPolarAlignmentSystemVM {
        bool Connected { get; }
        bool DoAutomatedAdjustments { get; set; }
        double AutomatedAdjustmentSettleTime { get; set; }

        Task Connect();
        void Disconnect();
        Task<bool> TryNudgeX(float position, CancellationToken token);
        Task<bool> TryNudgeY(float position, CancellationToken token);

        /// <summary>
        /// Relative nudge issued by the automated fine-approach loop. Systems without a
        /// specific policy behave exactly like <see cref="TryNudgeX"/>; a system that models
        /// its own backlash (OAPA) plans the compensation into the move instead.
        /// </summary>
        Task<bool> TryFineNudgeX(float position, CancellationToken token);

        /// <summary>Altitude counterpart of <see cref="TryFineNudgeX"/>.</summary>
        Task<bool> TryFineNudgeY(float position, CancellationToken token);
        Task NudgeX(float position, CancellationToken token);
        Task NudgeY(float position, CancellationToken token);
        void RaiseAllPropertiesChanged();
    }
}
