using NINA.Plugins.PolarAlignment.OAPA;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>The OAPA view model with its controller supplied by the test, through the protected member a derived system would use.</summary>
    internal sealed class OapaTestVm : UniversalPolarAlignmentOAPAVM {

        // No dialog in a test: a move cap above 120' is confirmed unless a test says otherwise.
        public OapaTestVm() : base(null) {
            ConfirmLargeMoveCap = _ => true;
        }

        public IPolarAlignmentSystem Hardware {
            get => upa;
            set => upa = value;
        }

        public IOapaCalibrationSolver Solver {
            set => calibrationSolver = value;
        }
    }
}
