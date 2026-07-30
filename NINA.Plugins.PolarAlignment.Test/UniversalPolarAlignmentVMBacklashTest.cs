using FluentAssertions;
using NINA.Plugins.PolarAlignment.Avalon;
using NINA.Plugins.PolarAlignment.OAPA;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Exercises the backlash-clearing wiring through the production movement paths of the
    /// real view models, with a recording fake standing in for the controller hardware.
    /// </summary>
    public class UniversalPolarAlignmentVMBacklashTest {

        private sealed class FakeSystem : IPolarAlignmentSystem {
            public readonly List<(Axis axis, float move)> RelativeMoves = new();
            public readonly List<(Axis axis, float target)> AbsoluteMoves = new();

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
                AbsoluteMoves.Add((axis, position));
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

        private static (UniversalPolarAlignmentOAPAVM vm, FakeSystem system) OapaVm(float xCompensation, float yCompensation) {
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null);
            var system = new FakeSystem();
            vm.upa = system;
            vm.ReverseAzimuth = false;
            vm.ReverseAltitude = false;
            vm.XBacklashCompensation = xCompensation;
            vm.YBacklashCompensation = yCompensation;
            return (vm, system);
        }

        [Test]
        public async Task TryNudgeY_OnReversal_ClearsBacklashWithAltitudeCompensation() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f);

            (await vm.TryNudgeY(15, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();

            (await vm.TryNudgeY(-15, CancellationToken.None)).Should().BeTrue();

            var expected = BacklashCompensationPlanner.CreateSequence(5f, LastDirection.Negative);
            system.RelativeMoves.Should().Equal(
                (Axis.YAxis, -15f),
                (Axis.YAxis, expected.FirstMove),
                (Axis.YAxis, expected.SecondMove));
        }

        [Test]
        public async Task TryNudgeY_SameDirection_DoesNotClear() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f);

            await vm.TryNudgeY(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeY(15, CancellationToken.None);

            system.RelativeMoves.Should().Equal((Axis.YAxis, 15f));
        }

        [Test]
        public async Task MoveY_OnReversal_ClearsBacklashWithAltitudeCompensation() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f);

            await vm.TryNudgeY(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            vm.TargetPositionY = -100;
            await vm.MoveY(CancellationToken.None);

            var expected = BacklashCompensationPlanner.CreateSequence(5f, LastDirection.Negative);
            system.AbsoluteMoves.Should().Equal((Axis.YAxis, -100f));
            system.RelativeMoves.Should().Equal(
                (Axis.YAxis, expected.FirstMove),
                (Axis.YAxis, expected.SecondMove));
        }

        [Test]
        public async Task TryNudgeX_OnReversal_SequenceUnchangedByYWiring() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f);

            await vm.TryNudgeX(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeX(-15, CancellationToken.None);

            var expected = BacklashCompensationPlanner.CreateSequence(3f, LastDirection.Negative);
            system.RelativeMoves.Should().Equal(
                (Axis.XAxis, -15f),
                (Axis.XAxis, expected.FirstMove),
                (Axis.XAxis, expected.SecondMove));
        }

        [Test]
        public async Task TryNudgeY_AvalonDefaultPolicy_NeverClears() {
            // The Avalon UPAS altitude axis does not use backlash compensation: the base
            // policy must keep Y reversals as plain moves even with X compensation set.
            var vm = new UniversalPolarAlignmentVM(null);
            var system = new FakeSystem();
            vm.upa = system;
            vm.ReverseAltitude = false;
            vm.XBacklashCompensation = 3f;

            await vm.TryNudgeY(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeY(-15, CancellationToken.None);

            system.RelativeMoves.Should().Equal((Axis.YAxis, -15f));
        }

        [Test]
        public async Task TryNudgeY_ZeroCompensation_DoesNotClear() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 0f);

            await vm.TryNudgeY(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeY(-15, CancellationToken.None);

            system.RelativeMoves.Should().Equal((Axis.YAxis, -15f));
        }
    }
}
