using FluentAssertions;
using NINA.Plugins.PolarAlignment.Avalon;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Exercises the backlash-clearing wiring through the production movement paths of the
    /// real view models, with a dead-travel fake standing in for the controller hardware.
    ///
    /// The assertions are on the PHYSICAL arrival position, not on which commands were
    /// emitted: a compensation sequence is only correct if the mechanism actually lands on
    /// the commanded target. The previous zero-sum pair (−B then +B in the new direction)
    /// passed a command-shape test while moving the mechanism nowhere — both of its legs
    /// reversed and paid the play, so the target move's original shortfall survived it.
    /// </summary>
    public class UniversalPolarAlignmentVMBacklashTest {

        /// <summary>
        /// Controller fake with real dead travel: on every direction change the first
        /// <see cref="DeadbandArcmin"/> of commanded motion re-engages the drivetrain and
        /// moves nothing. Starts engaged positive.
        /// </summary>
        private sealed class FakeSystem : IPolarAlignmentSystem {
            public readonly List<(Axis axis, float move)> RelativeMoves = new();
            public readonly List<(Axis axis, float target)> AbsoluteMoves = new();

            public float DeadbandArcmin { get; init; }
            public double PhysicalX { get; private set; }
            public double PhysicalY { get; private set; }
            private double engagementX;
            private double engagementY;

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

            public FakeSystem(float deadbandArcmin = 0f) {
                DeadbandArcmin = deadbandArcmin;
                engagementX = deadbandArcmin;
                engagementY = deadbandArcmin;
            }

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
                RelativeMoves.Add((axis, position));
                Travel(axis, position);
                return Task.CompletedTask;
            }

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
                // The fake models relative physics only; absolute moves are recorded and
                // treated as a relative excursion of the commanded size for engagement.
                AbsoluteMoves.Add((axis, position));
                Travel(axis, position);
                return Task.CompletedTask;
            }

            private void Travel(Axis axis, float signedMotion) {
                double d = signedMotion;
                if (axis == Axis.XAxis) {
                    (PhysicalX, engagementX) = Advance(PhysicalX, engagementX, d);
                    if (d != 0) { XLastDirection = d >= 0 ? LastDirection.Positive : LastDirection.Negative; }
                } else if (axis == Axis.YAxis) {
                    (PhysicalY, engagementY) = Advance(PhysicalY, engagementY, d);
                    if (d != 0) { YLastDirection = d >= 0 ? LastDirection.Positive : LastDirection.Negative; }
                }
            }

            private (double physical, double engagement) Advance(double physical, double engagement, double d) {
                if (d > 0) {
                    var eaten = Math.Min(DeadbandArcmin - engagement, d);
                    return (physical + d - eaten, engagement + eaten);
                }
                if (d < 0) {
                    var eaten = Math.Min(engagement, -d);
                    return (physical - (-d - eaten), engagement - eaten);
                }
                return (physical, engagement);
            }

            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }
        }

        private static (UniversalPolarAlignmentOAPAVM vm, FakeSystem system) OapaVm(float xCompensation, float yCompensation, float deadband) {
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, null);
            var system = new FakeSystem(deadband);
            vm.upa = system;
            vm.ReverseAzimuth = false;
            vm.ReverseAltitude = false;
            vm.XBacklashCompensation = xCompensation;
            vm.YBacklashCompensation = yCompensation;
            return (vm, system);
        }

        [Test]
        public async Task TryNudgeY_PositiveToNegativeReversal_ArrivesAtTheCommandedTarget() {
            // Mechanism with 5' of real play, compensation configured at 5'. +15 lands at
            // +15 (already engaged positive). −15 crosses the play: the raw move only
            // travels 10', and the compensation must recover the missing 5' — physically.
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f, deadband: 5f);

            (await vm.TryNudgeY(15, CancellationToken.None)).Should().BeTrue();
            system.PhysicalY.Should().BeApproximately(15.0, 0.01);

            (await vm.TryNudgeY(-15, CancellationToken.None)).Should().BeTrue();

            system.PhysicalY.Should().BeApproximately(0.0, 0.01,
                "a −15' nudge from +15' must physically land on 0' once the compensation recovers the play");
        }

        [Test]
        public async Task TryNudgeY_NegativeToPositiveReversal_ArrivesAtTheCommandedTarget() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f, deadband: 5f);

            await vm.TryNudgeY(15, CancellationToken.None);
            await vm.TryNudgeY(-15, CancellationToken.None);
            system.PhysicalY.Should().BeApproximately(0.0, 0.01);

            (await vm.TryNudgeY(15, CancellationToken.None)).Should().BeTrue();

            system.PhysicalY.Should().BeApproximately(15.0, 0.01,
                "the reversal back to positive pays the play again and the compensation must recover it again");
        }

        [Test]
        public async Task TryNudgeX_BothReversalDirections_ArriveAtTheCommandedTarget() {
            // Same physics on the azimuth axis with its own compensation value.
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f, deadband: 3f);

            await vm.TryNudgeX(15, CancellationToken.None);
            system.PhysicalX.Should().BeApproximately(15.0, 0.01);

            await vm.TryNudgeX(-15, CancellationToken.None);
            system.PhysicalX.Should().BeApproximately(0.0, 0.01, "positive-to-negative reversal must land exactly");

            await vm.TryNudgeX(15, CancellationToken.None);
            system.PhysicalX.Should().BeApproximately(15.0, 0.01, "negative-to-positive reversal must land exactly");
        }

        [Test]
        public async Task TheCompensation_IsASingleMoveInTheNewDirection_NotAZeroSumPair() {
            // Command shape, as documentation of the fix: after a reversal the clearing is
            // one move of the full compensation continuing in the new direction. The old
            // pair (−B, +B) reversed twice, paid the play twice, and left the shortfall.
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f, deadband: 5f);

            await vm.TryNudgeY(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeY(-15, CancellationToken.None);

            system.RelativeMoves.Should().Equal(
                (Axis.YAxis, -15f),
                (Axis.YAxis, -5f));
        }

        [Test]
        public async Task TryNudgeY_SameDirection_DoesNotClear() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f, deadband: 5f);

            await vm.TryNudgeY(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeY(15, CancellationToken.None);

            system.RelativeMoves.Should().Equal((Axis.YAxis, 15f));
            system.PhysicalY.Should().BeApproximately(30.0, 0.01, "same-direction moves pay no play and need no clearing");
        }

        [Test]
        public async Task MoveY_OnReversal_ClearsWithTheSingleCompensationMove() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f, deadband: 5f);

            await vm.TryNudgeY(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            vm.TargetPositionY = -100;
            await vm.MoveY(CancellationToken.None);

            system.AbsoluteMoves.Should().Equal((Axis.YAxis, -100f));
            system.RelativeMoves.Should().Equal((Axis.YAxis, -5f));
        }

        [Test]
        public async Task TryNudgeY_AvalonDefaultPolicy_NeverClears() {
            // The Avalon UPAS altitude axis does not use backlash compensation: the base
            // policy must keep Y reversals as plain moves even with X compensation set.
            var vm = new UniversalPolarAlignmentVM(null);
            var system = new FakeSystem(deadbandArcmin: 0f);
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
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 0f, deadband: 0f);

            await vm.TryNudgeY(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeY(-15, CancellationToken.None);

            system.RelativeMoves.Should().Equal((Axis.YAxis, -15f));
        }
    }
}
