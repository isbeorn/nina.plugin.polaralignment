using System;
using FluentAssertions;
using NINA.Plugins.PolarAlignment.Avalon;
using NINA.Plugins.PolarAlignment.OAPA;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// UPAS keeps the shared upstream backlash contract on every path - manual and
    /// automated fine-approach nudges restore the positive preload with the
    /// overtravel-and-return pair after any negative movement. OAPA routes both paths
    /// through its backlash-mode plan instead (the per-mode behavior itself is covered
    /// in OapaBacklashModeVmTest).
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

        private static (T vm, FakeSystem system) Prepare<T>(T vm, FakeSystem system) where T : UniversalPolarAlignmentBaseVM {
            vm.ReverseAzimuth = false;
            vm.XBacklashCompensation = 5f;
            // Symmetric by construction: these tests compare the shared clearing sequence
            // against the OAPA plan, not direction-dependent play, and the setting is
            // process-global so a value left by another test would leak in.
            if (vm is UniversalPolarAlignmentOAPAVM oapa) { oapa.XBacklashCompensationNegative = 5f; }
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
            var fake = new FakeSystem();
            var (vm, system) = Prepare(new UpasTestVm { Hardware = fake }, fake);

            await RunReversal(vm, system, fine: false, reversalMove: -0.5f);

            system.RelativeMoves.Should().Equal(
                new[] { (Axis.XAxis, -0.5f) }.Concat(ClearingSequence));
        }

        [Test]
        public async Task ManualAndFineNudge_OAPA_FollowTheSameModePlan() {
            // OAPA replaces the legacy excursion with its mode plan on both paths: under
            // the default Full mode a reversal is a single move extended by the backlash.
            var fake = new FakeSystem();
            var (vm, system) = Prepare(new OapaTestVm { Hardware = fake }, fake);
            ((UniversalPolarAlignmentOAPAVM)vm).XBacklashMode = OapaBacklashMode.Full;

            await RunReversal(vm, system, fine: false, reversalMove: -0.5f);
            system.RelativeMoves.Should().Equal((Axis.XAxis, -5.5f));

            system.RelativeMoves.Clear();
            (await vm.TryNudgeX(15f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();
            (await vm.TryFineNudgeX(-0.5f, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Should().Equal((Axis.XAxis, -5.5f));
        }

        [Test]
        public async Task FineNudge_UPAS_SubCompensationReversal_StillClears() {
            var fake = new FakeSystem();
            var (vm, system) = Prepare(new UpasTestVm { Hardware = fake }, fake);

            await RunReversal(vm, system, fine: true, reversalMove: -0.5f);

            system.RelativeMoves.Should().Equal(
                new[] { (Axis.XAxis, -0.5f) }.Concat(ClearingSequence));
        }
    }
}
