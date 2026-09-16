using FluentAssertions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The relative-move path is now a virtual the OAPA view model overrides. For every system
    /// that does not override it the command stream must be the one this plugin has always
    /// emitted, so these tests pin it exactly: what reaches the controller, in which order.
    ///
    /// A system that overrides nothing stands in for the Avalon UPAS and for the manual
    /// controls, which share the same path.
    /// </summary>
    public class SharedRelativeMoveContractTest {

        private sealed class RecordingSystem : IPolarAlignmentSystem {
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
                var direction = position >= 0 ? LastDirection.Positive : LastDirection.Negative;
                switch (axis) {
                    case Axis.XAxis: XLastDirection = direction; break;
                    case Axis.YAxis: YLastDirection = direction; break;
                    default: ZLastDirection = direction; break;
                }
                return Task.CompletedTask;
            }

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
                AbsoluteMoves.Add((axis, position));
                return Task.CompletedTask;
            }

            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }
        }

        private static (UpasTestVm vm, RecordingSystem system) Vm(float azimuthCompensation) {
            var system = new RecordingSystem();
            var vm = new UpasTestVm { Hardware = system };
            vm.ReverseAzimuth = false;
            vm.ReverseAltitude = false;
            vm.XBacklashCompensation = azimuthCompensation;
            return (vm, system);
        }

        [Test]
        public async Task APositiveAzimuthNudge_IsTheMoveAlone() {
            // The axis is left engaged positive, so a positive move pays no play and needs no
            // compensation: one command, exactly as commanded.
            var (vm, system) = Vm(azimuthCompensation: 3f);

            (await vm.TryNudgeX(15f, CancellationToken.None)).Should().BeTrue();

            system.RelativeMoves.Should().Equal((Axis.XAxis, 15f));
        }

        [Test]
        public async Task ANegativeAzimuthNudge_IsFollowedByTheOvertravelPair() {
            // The historical one-sided scheme: the move, then out by the compensation and back,
            // which restores the positive preload. Three commands, in this order.
            var (vm, system) = Vm(azimuthCompensation: 3f);
            await vm.TryNudgeX(15f, CancellationToken.None);
            system.RelativeMoves.Clear();

            (await vm.TryNudgeX(-10f, CancellationToken.None)).Should().BeTrue();

            system.RelativeMoves.Should().Equal(
                (Axis.XAxis, -10f), (Axis.XAxis, -3f), (Axis.XAxis, 3f));
        }

        [Test]
        public async Task WithNoCompensationConfigured_EvenAReversalIsTheMoveAlone() {
            var (vm, system) = Vm(azimuthCompensation: 0f);
            await vm.TryNudgeX(15f, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeX(-10f, CancellationToken.None);

            system.RelativeMoves.Should().Equal((Axis.XAxis, -10f));
        }

        [Test]
        public async Task TheAltitudeAxis_IsNeverCompensated_ByTheDefaultPolicy() {
            // Only azimuth carries a configured backlash in the shared policy, and that is what
            // the Avalon UPAS expects: a reversal on altitude is a plain move at any size.
            var (vm, system) = Vm(azimuthCompensation: 3f);
            await vm.TryNudgeY(15f, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeY(-10f, CancellationToken.None);

            system.RelativeMoves.Should().Equal((Axis.YAxis, -10f));
        }

        [Test]
        public async Task AnAbsoluteAltitudeMove_EmitsNothingBeyondTheMove() {
            // The altitude clearing added for OAPA must stay invisible here: with no altitude
            // compensation the planner returns no moves at all.
            var (vm, system) = Vm(azimuthCompensation: 3f);
            await vm.TryNudgeY(-10f, CancellationToken.None);
            system.RelativeMoves.Clear();
            vm.TargetPositionY = -20f;

            await vm.MoveY(CancellationToken.None);

            system.AbsoluteMoves.Should().Equal((Axis.YAxis, -20f));
            system.RelativeMoves.Should().BeEmpty();
        }

        [Test]
        public async Task TheFineNudge_TakesTheSamePath_ForASystemThatDoesNotOverrideIt() {
            // The automated fine-approach path defaults to the manual one, so a system with no
            // policy of its own cannot tell the two apart.
            var (vm, system) = Vm(azimuthCompensation: 3f);
            await vm.TryNudgeX(15f, CancellationToken.None);
            system.RelativeMoves.Clear();

            (await vm.TryFineNudgeX(-10f, CancellationToken.None)).Should().BeTrue();

            system.RelativeMoves.Should().Equal(
                (Axis.XAxis, -10f), (Axis.XAxis, -3f), (Axis.XAxis, 3f));
        }
    }
}
