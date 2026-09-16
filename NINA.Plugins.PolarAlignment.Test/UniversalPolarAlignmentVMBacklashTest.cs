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
    /// real view models, with a recording fake standing in for the controller hardware.
    /// </summary>
    public class UniversalPolarAlignmentVMBacklashTest {

        /// <summary>
        /// Controller fake, optionally with real dead travel: on every direction change
        /// the first <see cref="DeadbandArcmin"/> of commanded motion re-engages the
        /// drivetrain and moves nothing. Starts engaged positive. With the default
        /// deadband of zero it behaves as the plain recording fake.
        /// </summary>
        private sealed class FakeSystem : IPolarAlignmentSystem {
            public readonly List<(Axis axis, float move)> RelativeMoves = new();
            public readonly List<(Axis axis, float target)> AbsoluteMoves = new();

            public float DeadbandArcmin { get; }
            public double PhysicalX { get; private set; }
            public double PhysicalY { get; private set; }
            private double engagementX;
            private double engagementY;

            public FakeSystem(float deadbandArcmin = 0f) {
                DeadbandArcmin = deadbandArcmin;
                engagementX = deadbandArcmin;
                engagementY = deadbandArcmin;
            }

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
                // The fake models relative physics only; the absolute target is applied
                // as a relative excursion of the commanded size for engagement purposes.
                AbsoluteMoves.Add((axis, position));
                Track(axis, position);
                return Task.CompletedTask;
            }

            private void Track(Axis axis, float signedMotion) {
                var direction = signedMotion >= 0 ? LastDirection.Positive : LastDirection.Negative;
                switch (axis) {
                    case Axis.XAxis:
                        (PhysicalX, engagementX) = Advance(PhysicalX, engagementX, signedMotion);
                        XLastDirection = direction;
                        break;
                    case Axis.YAxis:
                        (PhysicalY, engagementY) = Advance(PhysicalY, engagementY, signedMotion);
                        YLastDirection = direction;
                        break;
                    case Axis.ZAxis: ZLastDirection = direction; break;
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

        private static (UniversalPolarAlignmentOAPAVM vm, FakeSystem system) OapaVm(float xCompensation, float yCompensation) {
            var system = new FakeSystem();
            var vm = new OapaTestVm { Hardware = system };
            vm.ReverseAzimuth = false;
            vm.ReverseAltitude = false;
            vm.XBacklashCompensation = xCompensation;
            vm.YBacklashCompensation = yCompensation;
            // Symmetric by construction: the setting is process-global, so leaving the
            // negative direction unset would let another test's value decide these plans.
            vm.XBacklashCompensationNegative = xCompensation;
            vm.YBacklashCompensationNegative = yCompensation;
            vm.XBacklashMode = OapaBacklashMode.Full;
            vm.YBacklashMode = OapaBacklashMode.Full;
            return (vm, system);
        }

        [Test]
        public async Task TryNudgeY_OnReversal_FoldsTheAltitudeCompensationIntoTheMove() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f);

            (await vm.TryNudgeY(15, CancellationToken.None)).Should().BeTrue();
            system.RelativeMoves.Clear();

            (await vm.TryNudgeY(-15, CancellationToken.None)).Should().BeTrue();

            // Full mode: a single move of d+B, no out-and-back excursion.
            system.RelativeMoves.Should().Equal((Axis.YAxis, -20f));
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
        public async Task MoveY_Absolute_RestoresThePositivePreload_WithTheOvertravelPair() {
            // The backlash modes govern relative nudges; absolute moves keep the shared
            // upstream contract on every system: after a negative movement the axis is
            // brought back under positive preload with an overtravel-and-return pair.
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f);

            await vm.TryNudgeY(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            vm.TargetPositionY = -100;
            await vm.MoveY(CancellationToken.None);

            system.AbsoluteMoves.Should().Equal((Axis.YAxis, -100f));
            system.RelativeMoves.Should().Equal((Axis.YAxis, -5f), (Axis.YAxis, 5f));
        }

        [Test]
        public async Task AvalonNudgeX_BothReversalDirections_PhysicallyLandOnTheTarget() {
            // The shared clearing path, against a mechanism with real dead travel equal
            // to the configured compensation. What matters is where the axis ends up,
            // not which commands were emitted: the one-sided scheme keeps the axis under
            // positive preload, so positive moves pay no play and negative moves are
            // followed by the overtravel-and-return pair.
            var system = new FakeSystem(deadbandArcmin: 3f);
            var vm = new UpasTestVm { Hardware = system };
            vm.ReverseAzimuth = false;
            vm.XBacklashCompensation = 3f;

            await vm.TryNudgeX(15, CancellationToken.None);
            system.PhysicalX.Should().BeApproximately(15.0, 0.01, "the axis starts engaged positive: a positive move pays no play");

            await vm.TryNudgeX(-15, CancellationToken.None);
            system.PhysicalX.Should().BeApproximately(0.0, 0.01,
                "positive-to-negative: the raw move travels 12', the overtravel leg recovers the lost 3' and the return re-engages the positive preload");

            await vm.TryNudgeX(15, CancellationToken.None);
            system.PhysicalX.Should().BeApproximately(15.0, 0.01,
                "from the restored positive preload the positive move pays no play again");
        }

        [Test]
        public async Task TryNudgeX_OnReversal_FoldsTheAzimuthCompensationIntoTheMove() {
            var (vm, system) = OapaVm(xCompensation: 3f, yCompensation: 5f);

            await vm.TryNudgeX(15, CancellationToken.None);
            system.RelativeMoves.Clear();

            await vm.TryNudgeX(-15, CancellationToken.None);

            system.RelativeMoves.Should().Equal((Axis.XAxis, -18f));
        }

        [Test]
        public async Task TryNudgeY_AvalonDefaultPolicy_NeverClears() {
            // The Avalon UPAS altitude axis does not use backlash compensation: the base
            // policy must keep Y reversals as plain moves even with X compensation set.
            var system = new FakeSystem();
            var vm = new UpasTestVm { Hardware = system };
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
