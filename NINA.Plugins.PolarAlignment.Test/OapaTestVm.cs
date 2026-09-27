using NINA.Plugins.PolarAlignment.OAPA;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>The OAPA view model with its controller supplied by the test, through the protected member a derived system would use.</summary>
    internal sealed class OapaTestVm : UniversalPolarAlignmentOAPAVM {

        public OapaTestVm() : base(null) { }

        public IPolarAlignmentSystem Hardware {
            get => upa;
            set => upa = value;
        }

        public IOapaCalibrationSolver Solver {
            set => calibrationSolver = value;
        }
    }
}
