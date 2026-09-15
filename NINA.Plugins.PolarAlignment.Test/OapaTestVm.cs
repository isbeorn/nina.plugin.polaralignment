using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugins.PolarAlignment.OAPA;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The OAPA view model with its controller and plate-solve boundary supplied by the test,
    /// through the protected members a derived system would use.
    /// </summary>
    internal sealed class OapaTestVm : UniversalPolarAlignmentOAPAVM {

        public OapaTestVm(ICameraMediator cameraMediator = null)
            : base(null, null, null, null, cameraMediator) { }

        public IPolarAlignmentSystem Hardware {
            get => upa;
            set => upa = value;
        }

        public IOapaCalibrationSolver Solver {
            set => calibrationSolver = value;
        }
    }
}
