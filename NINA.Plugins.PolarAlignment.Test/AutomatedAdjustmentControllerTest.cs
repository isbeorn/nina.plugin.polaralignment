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

            var result = RunClosedLoop(controller, plant, initialAzimuthErrorDegrees: 0.9, initialAltitudeErrorDegrees: 0.6, maxIterations: 16);

            result.FinalErrorDegrees.Should().BeLessThan(0.04);
            result.Iterations.Should().BeLessThan(16);
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
        public void AutomatedAdjustmentController_UsesCoarseXAxisProbeAndConservativeYAxisProbe() {
            var controller = new AutomatedAdjustmentController();
            controller.UpdateObservation(0.5, -0.3);

            var xProbe = controller.CreatePlan();
            xProbe.IsProbe.Should().BeTrue();
            xProbe.XMagnitude.Should().Be(10);
            xProbe.YMagnitude.Should().Be(0);

            controller.NoteSuccessfulExecution(xProbe);
            controller.UpdateObservation(0.4, -0.3);

            var yProbe = controller.CreatePlan();
            yProbe.IsProbe.Should().BeTrue();
            yProbe.XMagnitude.Should().Be(0);
            yProbe.YMagnitude.Should().Be(1);
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
