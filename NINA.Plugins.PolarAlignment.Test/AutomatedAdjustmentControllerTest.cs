using FluentAssertions;
using System;

namespace NINA.Plugins.PolarAlignment.Test {
    /// <summary>
    /// Closed-loop tests for the automated correction controller.
    ///
    /// These tests simulate the hardware as a local linear response:
    /// the controller issues X/Y commands, the synthetic mount changes the reported
    /// azimuth/altitude error, and the next solve feeds that observed error back.
    /// </summary>
    public class AutomatedAdjustmentControllerTest {
        [Test]
        public void AutomatedAdjustmentController_LearnsReversedAzimuthAxisAndConverges() {
            // X is intentionally reversed here: a positive X command makes the azimuth error worse.
            // The controller should identify that from the measured response and converge anyway.
            var controller = new AutomatedAdjustmentController();
            var plant = new[,] {
                { +0.08,  0.00 },
                {  0.00, -0.07 }
            };

            var result = RunClosedLoop(controller, plant, initialAzimuthErrorDegrees: 0.4, initialAltitudeErrorDegrees: -0.25, maxIterations: 12);

            result.FinalErrorDegrees.Should().BeLessThan(0.03);
            result.Iterations.Should().BeLessThan(12);
            controller.HasResponseModel.Should().BeTrue();
        }

        [TestCase(-0.08, -0.07)]
        [TestCase(-0.08, 0.07)]
        [TestCase(0.08, -0.07)]
        [TestCase(0.08, 0.07)]
        public void AutomatedAdjustmentController_LearnsAxisPolarityAndConverges(double azimuthDeltaPerXUnit, double altitudeDeltaPerYUnit) {
            var controller = new AutomatedAdjustmentController();
            var plant = new[,] {
                { azimuthDeltaPerXUnit,  0.00 },
                { 0.00, altitudeDeltaPerYUnit }
            };

            var result = RunClosedLoop(controller, plant, initialAzimuthErrorDegrees: 0.4, initialAltitudeErrorDegrees: -0.25, maxIterations: 12);

            result.FinalErrorDegrees.Should().BeLessThan(0.03);
            result.Iterations.Should().BeLessThan(12);
            controller.HasResponseModel.Should().BeTrue();
        }

        [Test]
        public void AutomatedAdjustmentController_LearnsPoorCalibrationAndAxisCrossCoupling() {
            // This plant is deliberately miscalibrated and cross-coupled:
            // one X unit changes the sky by far more than one arcminute, and each axis affects
            // both residual error components. The controller should still settle by learning A.
            var controller = new AutomatedAdjustmentController();
            var plant = new[,] {
                { -0.18, -0.03 },
                { +0.04, -0.11 }
            };

            var result = RunClosedLoop(controller, plant, initialAzimuthErrorDegrees: 0.9, initialAltitudeErrorDegrees: 0.6, maxIterations: 12);

            result.FinalErrorDegrees.Should().BeLessThan(0.04);
            result.Iterations.Should().BeLessThan(12);
            controller.SampleCount.Should().BeGreaterThanOrEqualTo(3);
        }

        [Test]
        public void AutomatedAdjustmentController_DoesNotLearnFromFailedMove() {
            // A failed nudge must not be treated as a valid identification sample.
            // Otherwise the controller would "learn" zero response and corrupt the model.
            var controller = new AutomatedAdjustmentController();
            controller.UpdateObservation(0.5, -0.3);

            var firstPlan = controller.CreatePlan();
            firstPlan.HasMovement.Should().BeTrue();
            firstPlan.IsProbe.Should().BeTrue();
            firstPlan.XMagnitude.Should().NotBe(0);

            controller.NoteFailedExecution();
            controller.UpdateObservation(0.5, -0.3);

            controller.SampleCount.Should().Be(0);

            var secondPlan = controller.CreatePlan();
            secondPlan.HasMovement.Should().BeTrue();
            secondPlan.IsProbe.Should().BeTrue();
            secondPlan.XMagnitude.Should().NotBe(0);
        }

        [Test]
        public void BacklashCompensationPlanner_PositiveDirection_EndsLoadedPositive() {
            // When the target direction is positive, backlash clearing should finish with
            // a positive preload so the final mechanical state matches the requested direction.
            var sequence = BacklashCompensationPlanner.CreateSequence(3f, LastDirection.Positive);

            sequence.FirstMove.Should().Be(-3f);
            sequence.SecondMove.Should().Be(3f);
        }

        [Test]
        public void BacklashCompensationPlanner_NegativeDirection_EndsLoadedNegative() {
            // The mirrored case of the test above: after a negative-direction reversal,
            // the backlash sequence should finish in the negative direction.
            var sequence = BacklashCompensationPlanner.CreateSequence(3f, LastDirection.Negative);

            sequence.FirstMove.Should().Be(3f);
            sequence.SecondMove.Should().Be(-3f);
        }

        [Test]
        public void AutomatedAdjustmentController_MaximumMoveMagnitude_IsClampedToConfigurableRange() {
            var controller = new AutomatedAdjustmentController();

            controller.MaximumMoveMagnitude = 50;
            controller.MaximumMoveMagnitude.Should().Be(AutomatedAdjustmentController.MaximumConfigurableMoveMagnitude);

            controller.MaximumMoveMagnitude = 0.1;
            controller.MaximumMoveMagnitude.Should().Be(AutomatedAdjustmentController.MinimumConfigurableMoveMagnitude);

            controller.MaximumMoveMagnitude = 12;
            controller.MaximumMoveMagnitude.Should().Be(12);
        }

        [Test]
        public void AutomatedAdjustmentController_ProbeMagnitude_ScalesWithLargeError() {
            // With a 2 degree (120') error and a raised cap, the identification probe should be
            // larger than the 1-unit default so the response stays observable above solve noise.
            var controller = new AutomatedAdjustmentController {
                MaximumMoveMagnitude = 20
            };
            controller.UpdateObservation(2.0, 0.0);

            var plan = controller.CreatePlan();

            plan.IsProbe.Should().BeTrue();
            Math.Abs(plan.XMagnitude + plan.YMagnitude).Should().BeApproximately(10.0, 0.001); // min(120 * 0.15, 20/2)
        }

        [Test]
        public void AutomatedAdjustmentController_ProbeMagnitude_StaysGentleNearThePole() {
            // Near the pole (3' error) the probe should remain at the conservative default.
            var controller = new AutomatedAdjustmentController();
            controller.UpdateObservation(0.05, 0.0);

            var plan = controller.CreatePlan();

            plan.IsProbe.Should().BeTrue();
            Math.Abs(plan.XMagnitude + plan.YMagnitude).Should().BeApproximately(1.0, 0.001);
        }

        [Test]
        public void AutomatedAdjustmentController_LargeError_ConvergesFasterWithRaisedCap() {
            // Brian's field report: with a multi-degree initial error the default 5-unit cap
            // needs many cycles. A raised cap must converge in fewer iterations on the same plant.
            var plant = new[,] {
                { -0.02, 0.00 },
                { 0.00, -0.02 }
            };

            var defaultCapController = new AutomatedAdjustmentController();
            var defaultCapResult = RunClosedLoop(defaultCapController, plant, initialAzimuthErrorDegrees: 1.5, initialAltitudeErrorDegrees: 1.0, maxIterations: 60);

            var raisedCapController = new AutomatedAdjustmentController { MaximumMoveMagnitude = 25 };
            var raisedCapResult = RunClosedLoop(raisedCapController, plant, initialAzimuthErrorDegrees: 1.5, initialAltitudeErrorDegrees: 1.0, maxIterations: 60);

            raisedCapResult.FinalErrorDegrees.Should().BeLessThan(0.04);
            raisedCapResult.Iterations.Should().BeLessThan(defaultCapResult.Iterations);
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
