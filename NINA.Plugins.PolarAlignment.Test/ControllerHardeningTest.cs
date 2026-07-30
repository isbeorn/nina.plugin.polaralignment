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
            var farController = new AutomatedAdjustmentController { AggressiveCorrections = true, MaximumMoveMagnitude = 20 };
            farController.UpdateObservation(3.0, 0.0);
            var farProbe = farController.CreatePlan();
            farProbe.IsProbe.Should().BeTrue();
            Math.Abs(farProbe.XMagnitude + farProbe.YMagnitude).Should().BeApproximately(10.0, 0.01);

            // Near the pole (3' error) the probe stays at the conservative default.
            var nearController = new AutomatedAdjustmentController { AggressiveCorrections = true, MaximumMoveMagnitude = 20 };
            nearController.UpdateObservation(0.05, 0.0);
            var nearProbe = nearController.CreatePlan();
            nearProbe.IsProbe.Should().BeTrue();
            Math.Abs(nearProbe.XMagnitude + nearProbe.YMagnitude).Should().BeApproximately(1.0, 0.01);
        }

        [Test]
        public void LegacyProfile_ProbeStaysAtDefault_RegardlessOfError() {
            // UPAS regression: without the aggressive profile the probe must stay at the
            // legacy 1.0 units even on a multi-degree error, where the aggressive profile
            // would raise it to cap/2 = 2.5.
            var controller = new AutomatedAdjustmentController();
            controller.UpdateObservation(2.0, 0.0);

            var probe = controller.CreatePlan();

            probe.IsProbe.Should().BeTrue();
            Math.Abs(probe.XMagnitude + probe.YMagnitude).Should().BeApproximately(1.0, 0.001);
        }

        [Test]
        public void LegacyProfile_NeverEmitsThe75PercentCandidate() {
            // Model learned from two clean probes: az response -0.1 per X unit, alt response
            // -0.1 per Y unit; residual (0.3, 0.3) -> raw least-squares command (3, 3), well
            // inside the cap so the scaling is directly visible in the chosen plan.
            var controller = new AutomatedAdjustmentController();
            controller.UpdateObservation(0.4, 0.4);
            controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(1.0, 0, true, "probe"));
            controller.UpdateObservation(0.3, 0.4);
            controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(0, 1.0, true, "probe"));
            controller.UpdateObservation(0.3, 0.3);

            var plan = controller.CreatePlan();

            // The legacy candidate set tops out at 50% of the raw solution; the aggressive
            // 75% candidate would win the prediction and produce (2.25, 2.25).
            plan.IsProbe.Should().BeFalse();
            plan.XMagnitude.Should().BeApproximately(1.5, 0.01);
            plan.YMagnitude.Should().BeApproximately(1.5, 0.01);
        }

        [Test]
        public void AggressiveProfile_KeepsThe75PercentCandidate() {
            var controller = new AutomatedAdjustmentController { AggressiveCorrections = true };
            controller.UpdateObservation(0.4, 0.4);
            controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(1.0, 0, true, "probe"));
            controller.UpdateObservation(0.3, 0.4);
            controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(0, 1.0, true, "probe"));
            controller.UpdateObservation(0.3, 0.3);

            var plan = controller.CreatePlan();

            plan.XMagnitude.Should().BeApproximately(2.25, 0.01);
            plan.YMagnitude.Should().BeApproximately(2.25, 0.01);
        }

        [Test]
        public void LegacyProfile_KeepsLegacySequence_OnInvertedCrossCoupledResponses() {
            // UPAS regression across the whole loop: inverted polarity on both axes plus
            // cross-coupling. Every probe must stay at the legacy 1.0 units and the loop
            // must still converge on 50%-scaled corrections alone.
            var plant = new[,] {
                { +0.06, +0.02 },
                { +0.015, +0.05 }
            };

            var controller = new AutomatedAdjustmentController();
            var azimuthError = 0.5;
            var altitudeError = 0.4;
            controller.UpdateObservation(azimuthError, altitudeError);

            var converged = false;
            for (var iteration = 0; iteration < 40; iteration++) {
                var plan = controller.CreatePlan();
                if (!plan.HasMovement) {
                    break;
                }

                if (plan.IsProbe) {
                    Math.Abs(plan.XMagnitude + plan.YMagnitude).Should().BeApproximately(1.0, 0.001);
                }

                azimuthError += plant[0, 0] * plan.XMagnitude + plant[0, 1] * plan.YMagnitude;
                altitudeError += plant[1, 0] * plan.XMagnitude + plant[1, 1] * plan.YMagnitude;

                controller.NoteSuccessfulExecution(plan);
                controller.UpdateObservation(azimuthError, altitudeError);

                if (Math.Sqrt(azimuthError * azimuthError + altitudeError * altitudeError) < 0.05) {
                    converged = true;
                    break;
                }
            }

            converged.Should().BeTrue();
        }

        [Test]
        public void RaisedCap_ConvergesLargeErrorInFewerIterations() {
            // 1.8 degrees of initial error. With the stock 5-unit cap the loop cannot finish
            // within the iteration budget that the raised cap comfortably meets.
            var plant = new[,] {
                { -0.05, 0.00 },
                { 0.00, -0.05 }
            };

            var stock = RunClosedLoop(new AutomatedAdjustmentController { AggressiveCorrections = true }, plant, 1.5, 1.0, maxIterations: 20);
            var raised = RunClosedLoop(new AutomatedAdjustmentController { AggressiveCorrections = true, MaximumMoveMagnitude = 25 }, plant, 1.5, 1.0, maxIterations: 20);

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
        public void RunawayUnderLargeCorrections_IsClassifiedAsCalibrationSuspect() {
            // Moves whose measured sky response is large (6' per move) and keeps worsening
            // the error point at the actuator model: wrong calibration factors or backlash
            // compensation.
            var controller = new AutomatedAdjustmentController();
            var error = 0.5;
            controller.UpdateObservation(error, 0.0);

            for (var i = 0; i < 3; i++) {
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(5.0, 0.0, false, "corrective"));
                error += 0.1;
                controller.UpdateObservation(error, 0.0);
            }

            controller.RunawayDetected.Should().BeTrue();
            controller.RunawayLikelyEstimateDrift.Should().BeFalse();
            controller.CreatePlan().Reason.Should().Contain("Calibration factors");
        }

        [Test]
        public void RunawayUnderSubNoiseCorrections_IsClassifiedAsEstimateDrift() {
            // Field-observed failure mode: during a long fine phase the continuous error
            // estimate drifts on its own while the loop issues tiny corrections whose
            // measured sky response stays sub-arcminute. Blaming the calibration in that
            // case is wrong and sends the user down a rabbit hole; the correct remedy is
            // a fresh measurement.
            var controller = new AutomatedAdjustmentController();
            var error = 0.010;
            controller.UpdateObservation(error, 0.0);

            for (var i = 0; i < 3; i++) {
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(0.4, 0.0, false, "corrective"));
                error += 0.005;
                controller.UpdateObservation(error, 0.0);
            }

            controller.RunawayDetected.Should().BeTrue();
            controller.RunawayLikelyEstimateDrift.Should().BeTrue();
            controller.CreatePlan().Reason.Should().Contain("estimate has likely drifted");

            controller.Reset();
            controller.RunawayLikelyEstimateDrift.Should().BeFalse();
        }

        [Test]
        public void RunawayWithLargeObservedResponse_IsCalibrationSuspect_EvenForSmallDiagonalCommands() {
            // The circular case: commanded magnitude only maps to physical size when the
            // calibration is already right. Small diagonal (0.8, 0.8) commands that visibly
            // drive the measured error away by 6' per move are precisely a calibration
            // failure and must never be blamed on estimate drift.
            var controller = new AutomatedAdjustmentController();
            var error = 0.5;
            controller.UpdateObservation(error, 0.0);

            for (var i = 0; i < 3; i++) {
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(0.8, 0.8, false, "corrective"));
                error += 0.1;
                controller.UpdateObservation(error, 0.0);
            }

            controller.RunawayDetected.Should().BeTrue();
            controller.RunawayLikelyEstimateDrift.Should().BeFalse();
            controller.CreatePlan().Reason.Should().Contain("Calibration factors");
        }

        [Test]
        public void RunawayWithNegligibleObservedResponse_IsEstimateDrift_EvenForLargeCommands() {
            // A grossly overstated gear ratio: large commands barely move the mount, so the
            // sky response stays sub-arcminute while the error creeps up on its own. The
            // moves cannot have caused the worsening; the drift remedy (re-measure) is the
            // right call regardless of the commanded size.
            var controller = new AutomatedAdjustmentController();
            var error = 0.010;
            controller.UpdateObservation(error, 0.0);

            for (var i = 0; i < 3; i++) {
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(5.0, 0.0, false, "corrective"));
                error += 0.005;
                controller.UpdateObservation(error, 0.0);
            }

            controller.RunawayDetected.Should().BeTrue();
            controller.RunawayLikelyEstimateDrift.Should().BeTrue();
            controller.CreatePlan().Reason.Should().Contain("estimate has likely drifted");
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
