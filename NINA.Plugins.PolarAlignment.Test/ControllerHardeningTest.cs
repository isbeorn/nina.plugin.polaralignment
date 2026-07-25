using FluentAssertions;
using System;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Tests for the controller-hardening behaviors: configurable per-cycle limit,
    /// error-scaled probes, and runaway detection that only evaluates observations
    /// following an executed corrective plan.
    /// </summary>
    public class ControllerHardeningTest {

        [Test]
        public void MaximumMoveMagnitude_IsClampedToConfigurableBounds() {
            var controller = new AutomatedAdjustmentController();

            controller.MaximumMoveMagnitude = 0.2;
            controller.MaximumMoveMagnitude.Should().Be(AutomatedAdjustmentController.MinimumConfigurableMoveMagnitude);

            controller.MaximumMoveMagnitude = 500;
            controller.MaximumMoveMagnitude.Should().Be(AutomatedAdjustmentController.MaximumConfigurableMoveMagnitude);

            controller.MaximumMoveMagnitude = 12;
            controller.MaximumMoveMagnitude.Should().Be(12);
        }

        [Test]
        public void ProbeMagnitude_ScalesWithMeasuredError_AndStaysGentleNearThePole() {
            // Large residual: the probe must rise above the default so identification
            // is not drowned by solve noise. 3 degrees error, cap 20 -> 0.15*180 = 27,
            // clamped to cap/2 = 10.
            var farController = new AutomatedAdjustmentController { MaximumMoveMagnitude = 20 };
            farController.UpdateObservation(3.0, 0.0);
            var farProbe = farController.CreatePlan();
            farProbe.IsProbe.Should().BeTrue();
            Math.Abs(farProbe.XMagnitude + farProbe.YMagnitude).Should().BeApproximately(10.0, 0.01);

            // Near the pole (3' error) the probe stays at the conservative default.
            var nearController = new AutomatedAdjustmentController { MaximumMoveMagnitude = 20 };
            nearController.UpdateObservation(0.05, 0.0);
            var nearProbe = nearController.CreatePlan();
            nearProbe.IsProbe.Should().BeTrue();
            Math.Abs(nearProbe.XMagnitude + nearProbe.YMagnitude).Should().BeApproximately(1.0, 0.01);
        }

        [Test]
        public void RaisedCap_ConvergesLargeErrorInFewerIterations() {
            // 1.8 degrees of initial error. With the stock 5-unit cap the loop cannot finish
            // within the iteration budget that the raised cap comfortably meets.
            var plant = new[,] {
                { -0.05, 0.00 },
                { 0.00, -0.05 }
            };

            var stock = RunClosedLoop(new AutomatedAdjustmentController(), plant, 1.5, 1.0, maxIterations: 20);
            var raised = RunClosedLoop(new AutomatedAdjustmentController { MaximumMoveMagnitude = 25 }, plant, 1.5, 1.0, maxIterations: 20);

            raised.Iterations.Should().BeLessThan(stock.Iterations);
            raised.FinalErrorDegrees.Should().BeLessThan(0.05);
        }

        [Test]
        public void ConsecutiveWorseningCorrectiveMoves_TripRunawayAndStopMoves() {
            // Drive the detection state machine directly: three executed corrective moves
            // whose follow-up observation is materially worse must trip the runaway.
            var controller = new AutomatedAdjustmentController();
            var error = 0.5;
            controller.UpdateObservation(error, 0.0);

            for (var i = 0; i < 3; i++) {
                controller.RunawayDetected.Should().BeFalse();
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(1.0, 0.0, false, "corrective"));
                error += 0.1;
                controller.UpdateObservation(error, 0.0);
            }

            controller.RunawayDetected.Should().BeTrue();

            var halted = controller.CreatePlan();
            halted.HasMovement.Should().BeFalse();
            halted.Reason.Should().Contain("halted");

            controller.Reset();
            controller.RunawayDetected.Should().BeFalse();
        }

        [Test]
        public void ObservationsWithoutExecutedPlans_NeverTripRunaway() {
            // The manual-alignment path: solves arrive and the error may fluctuate or grow,
            // but no plan is ever executed, so runaway detection must stay silent.
            var controller = new AutomatedAdjustmentController();

            var error = 0.2;
            for (var i = 0; i < 20; i++) {
                error += 0.05;
                controller.UpdateObservation(error, 0.0);
            }

            controller.RunawayDetected.Should().BeFalse();
        }

        [Test]
        public void WorseningProbes_DoNotCountTowardRunaway() {
            // Probes intentionally move the mount and can worsen the error; only corrective
            // moves may accumulate toward the runaway threshold.
            var controller = new AutomatedAdjustmentController();
            var error = 0.5;
            controller.UpdateObservation(error, 0.0);

            for (var i = 0; i < 6; i++) {
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(1.0, 0.0, true, "probe"));
                error += 0.05;
                controller.UpdateObservation(error, 0.0);
            }

            controller.RunawayDetected.Should().BeFalse();
        }

        [Test]
        public void ImprovingMove_ResetsTheWorseningStreak() {
            // Two worsening correctives, one improving, two more worsening: the streak
            // restarts after the improvement and never reaches the threshold of three.
            var controller = new AutomatedAdjustmentController();
            var error = 0.5;
            controller.UpdateObservation(error, 0.0);

            var deltas = new[] { +0.1, +0.1, -0.2, +0.1, +0.1 };
            foreach (var delta in deltas) {
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(1.0, 0.0, false, "corrective"));
                error = Math.Max(0.05, error + delta);
                controller.UpdateObservation(error, 0.0);
            }

            controller.RunawayDetected.Should().BeFalse();
        }

        [Test]
        public void ShouldClear_OnlyOnReversalWithMoveAtLeastCompensation() {
            // No direction change: never clear.
            BacklashCompensationPlanner.ShouldClear(10f, 5f, LastDirection.Positive, LastDirection.Positive).Should().BeFalse();
            // No compensation configured: nothing to clear.
            BacklashCompensationPlanner.ShouldClear(10f, 0f, LastDirection.Positive, LastDirection.Negative).Should().BeFalse();
            // Reversal with a move larger than the compensation: clear.
            BacklashCompensationPlanner.ShouldClear(6f, 5f, LastDirection.Positive, LastDirection.Negative).Should().BeTrue();
            // Reversal with a sub-compensation nudge: skipping avoids the out-and-back
            // excursion that injects more error than the nudge removes.
            BacklashCompensationPlanner.ShouldClear(0.5f, 5f, LastDirection.Positive, LastDirection.Negative).Should().BeFalse();
            // Manual absolute moves pass float.MaxValue and always clear on reversal.
            BacklashCompensationPlanner.ShouldClear(float.MaxValue, 5f, LastDirection.Negative, LastDirection.Positive).Should().BeTrue();
        }

        private static ClosedLoopResult RunClosedLoop(AutomatedAdjustmentController controller,
                                                      double[,] plant,
                                                      double initialAzimuthErrorDegrees,
                                                      double initialAltitudeErrorDegrees,
                                                      int maxIterations) {
            var azimuthError = initialAzimuthErrorDegrees;
            var altitudeError = initialAltitudeErrorDegrees;

            controller.UpdateObservation(azimuthError, altitudeError);

            for (var iteration = 0; iteration < maxIterations; iteration++) {
                var plan = controller.CreatePlan();
                if (!plan.HasMovement) {
                    break;
                }

                azimuthError += plant[0, 0] * plan.XMagnitude + plant[0, 1] * plan.YMagnitude;
                altitudeError += plant[1, 0] * plan.XMagnitude + plant[1, 1] * plan.YMagnitude;

                controller.NoteSuccessfulExecution(plan);
                controller.UpdateObservation(azimuthError, altitudeError);

                var totalError = Math.Sqrt(azimuthError * azimuthError + altitudeError * altitudeError);
                if (totalError < 0.02) {
                    return new ClosedLoopResult(totalError, iteration + 1);
                }
            }

            return new ClosedLoopResult(Math.Sqrt(azimuthError * azimuthError + altitudeError * altitudeError), maxIterations);
        }

        private sealed class ClosedLoopResult {
            public ClosedLoopResult(double finalErrorDegrees, int iterations) {
                FinalErrorDegrees = finalErrorDegrees;
                Iterations = iterations;
            }

            public double FinalErrorDegrees { get; }
            public int Iterations { get; }
        }
    }
}
