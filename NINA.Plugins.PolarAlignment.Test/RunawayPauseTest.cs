using FluentAssertions;
using Nito.AsyncEx;
using NINA.Plugins.PolarAlignment.Instructions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The runaway halt must pause the alignment exactly once, stay stoppable while
    /// paused, and never issue another automated move - while the solve loop keeps
    /// running for display-only/manual operation after a resume.
    /// </summary>
    public class RunawayPauseTest {

        [Test]
        public void PauseGate_FiresExactlyOnce_OnTheFirstHaltedCycle() {
            var gate = new RunawayPauseGate();

            // Healthy cycles never pause; the first halted cycle pauses; later halted
            // cycles never re-pause, so a user resume sticks in display-only operation.
            var halted = new[] { false, false, true, true, true };
            var fired = new List<bool>();
            foreach (var cycle in halted) {
                fired.Add(gate.ShouldPause(cycle));
            }

            fired.Should().Equal(false, false, true, false, false);
        }

        [Test]
        public async Task MoveCloser_AfterRunawayHalt_IssuesNoFurtherMoves() {
            var (vm, system) = PrepareHarness();
            TripRunaway(vm.automatedAdjustmentController);

            await vm.MoveCloser(null, CancellationToken.None);

            system.RelativeMoves.Should().BeEmpty();
            system.AbsoluteMoves.Should().BeEmpty();
            vm.AutomatedAdjustmentsHalted.Should().BeTrue("the halt must persist across cycles");
        }

        [Test]
        public async Task MoveCloser_WithoutHalt_DoesIssueMoves_ProvingTheHarnessBites() {
            var (vm, system) = PrepareHarness();
            vm.automatedAdjustmentController.UpdateObservation(0.5, 0.0);

            await vm.MoveCloser(null, CancellationToken.None);

            // Same harness, no runaway: the controller issues its first probe move.
            system.RelativeMoves.Should().NotBeEmpty();
        }

        [Test]
        public async Task WaitWhilePaused_IsUnblockedByCancellation() {
            var pauseTS = new PauseTokenSource { IsPaused = true };
            using var cts = new CancellationTokenSource();

            var wait = pauseTS.Token.WaitWhilePausedAsync(cts.Token);
            wait.IsCompleted.Should().BeFalse("the wait must block while paused");

            cts.Cancel();
            Func<Task> act = () => wait;
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        private static (TPAPAVM vm, FakeSystem system) PrepareHarness() {
            var oapa = new UniversalPolarAlignmentOAPAVM(null);
            var system = new FakeSystem();
            oapa.upa = system;
            PolarAlignmentPlugin.UniversalPolarAlignmentVM = new Avalon.UniversalPolarAlignmentVM(null);
            PolarAlignmentPlugin.UniversalPolarAlignmentOAPAVM = oapa;
            Properties.Settings.Default.SelectedPolarAlignmentSystem = "OAPA";
            Properties.Settings.Default.DoAutomatedAdjustments = true;
            Properties.Settings.Default.UseContinuousErrorEstimator = false;
            Properties.Settings.Default.AutomatedAdjustmentSettleTime = 0;
            oapa.ReverseAzimuth = false;
            oapa.ReverseAltitude = false;
            oapa.XBacklashCompensation = 0;

            var vm = new TPAPAVM(null, null) {
                PolarErrorDetermination = OapaCorrectionCeilingPathTest.BuildErrorDetermination(0.5)
            };
            return (vm, system);
        }

        private static void TripRunaway(AutomatedAdjustmentController controller) {
            var error = 0.5;
            controller.UpdateObservation(error, 0.0);
            for (var i = 0; i < 3; i++) {
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(1.0, 0.0, false, "corrective"));
                error += 0.1;
                controller.UpdateObservation(error, 0.0);
            }
            controller.RunawayDetected.Should().BeTrue();
        }

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
    }
}
