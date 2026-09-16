using NINA.Plugins.PolarAlignment.Avalon;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The Avalon UPAS view model with its controller supplied by the test, through the
    /// protected member a derived system would use. Mirrors <see cref="OapaTestVm"/>.
    /// </summary>
    internal sealed class UpasTestVm : UniversalPolarAlignmentVM {

        public UpasTestVm() : base(null) { }

        public IPolarAlignmentSystem Hardware {
            get => upa;
            set => upa = value;
        }
    }
}
