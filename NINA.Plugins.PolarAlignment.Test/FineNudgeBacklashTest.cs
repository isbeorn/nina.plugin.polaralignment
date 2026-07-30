using FluentAssertions;
using NINA.Plugins.PolarAlignment.Avalon;
using NINA.Plugins.PolarAlignment.OAPA;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The skip-clearing policy for sub-compensation nudges belongs exclusively to the
    /// OAPA automated fine-approach path. Manual nudges - on every system - and the UPAS
    /// automated path must keep the legacy behavior: always clear backlash on reversal.
    /// </summary>
    public class FineNudgeBacklashTest {

        private sealed class FakeSystem : IPolarAlignmentSystem {
            public readonly List<(Axis axis, float move)> RelativeMoves = new();

            public bool Connected => true;
            public string Status => "Idle";
            public float XPosition1 => 0;
            public float YPosition1 => 0;
            public float ZPosition1 => 0;
            public float XGearRatio { get; set; } = 1;
            public float YGearRatio { get; set; } = 1;
            public float ZGearRatio { get; set; } = 1;
            public LastDirection XLastDirection { get; private set; } = LastDirection.Positive;
            public LastDirection YLastDirection { get; private set; } = LastDirection.Positive;
            public LastDirection ZLastDirection { get; private set; } = LastDirection.Positive;

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
                RelativeMoves.Add((axis, position));
                Track(axis, position);
                return Task.CompletedTask;
            }

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
                Track(axis, position);
                return Task.CompletedTask;
            }

            private void Track(Axis axis, float signedMotion) {
                var direction = signedMotion >= 0 ? LastDirection.Positive : LastDirection.Negative;
                switch (axis) {
                    case Axis.XAxis: XLastDirection = direction; break;
                    case Axis.YAxis: YLastDirection = direction; break;
                    case Axis.ZAxis: ZLastDirection = direction; break;
                }
            }

            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }
        }

        private static readonly (Axis, float)[] ClearingSequence = BuildClearingSequence();

        private static (Axis, float)[] BuildClearingSequence() {
            var sequence = BacklashCompensationPlanner.CreateSequence(5f, LastDirection.Negative);
            return new[] { (Axis.XAxis, sequence.FirstMove), (Axis.XAxis, sequence.SecondMove) };
        }

        private static (T vm, FakeSystem system) Prepare<T>(T vm) where T : UniversalPolarAlignmentBaseVM {
            var system = new FakeSystem();
            vm.upa = system;
            vm.ReverseAzimuth = false;
            vm.XBacklashCompensation = 5f;
            return (vm, system);
        }

        private static async Task<FakeSystem> RunReversal(UniversalPolarAlignmentBaseVM vm, FakeSystem system, bool fine, float reversalMove) {
            (await vm.TryNudgeX(15f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();
            var ok = fine
                ? await vm.TryFineNudgeX(reversalMove, CancellationToken.None)
                : await vm.TryNudgeX(reversalMove, CancellationToken.None);
            ok.Should().BeTrue();
            return system;
        }

        [Test]
        public async Task ManualNudge_UPAS_SubCompensationReversal_StillClears() {
            var (vm, system) = Prepare(new UniversalPolarAlignmentVM(null));

            await RunReversal(vm, system, fine: false, reversalMove: -0.5f);

            system.RelativeMoves.Should().Equal(
                new[] { (Axis.XAxis, -0.5f) }.Concat(ClearingSequence));
        }

        [Test]
        public async Task ManualNudge_OAPA_SubCompensationReversal_StillClears() {
            var (vm, system) = Prepare(new UniversalPolarAlignmentOAPAVM(null));

            await RunReversal(vm, system, fine: false, reversalMove: -0.5f);

            system.RelativeMoves.Should().Equal(
                new[] { (Axis.XAxis, -0.5f) }.Concat(ClearingSequence));
        }

        [Test]
        public async Task FineNudge_OAPA_SubCompensationReversal_SkipsClearing() {
            var (vm, system) = Prepare(new UniversalPolarAlignmentOAPAVM(null));

            await RunReversal(vm, system, fine: true, reversalMove: -0.5f);

            system.RelativeMoves.Should().Equal((Axis.XAxis, -0.5f));
        }

        [Test]
        public async Task FineNudge_OAPA_MoveAtLeastCompensation_Clears() {
            var (vm, system) = Prepare(new UniversalPolarAlignmentOAPAVM(null));

            await RunReversal(vm, system, fine: true, reversalMove: -6f);

            system.RelativeMoves.Should().Equal(
                new[] { (Axis.XAxis, -6f) }.Concat(ClearingSequence));
        }

        [Test]
        public async Task FineNudge_UPAS_SubCompensationReversal_StillClears() {
            var (vm, system) = Prepare(new UniversalPolarAlignmentVM(null));

            await RunReversal(vm, system, fine: true, reversalMove: -0.5f);

            system.RelativeMoves.Should().Equal(
                new[] { (Axis.XAxis, -0.5f) }.Concat(ClearingSequence));
        }
    }
}
