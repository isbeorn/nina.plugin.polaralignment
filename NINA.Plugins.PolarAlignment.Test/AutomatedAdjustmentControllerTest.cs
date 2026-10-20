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

        [TestCase(-3.0, -0.005, 0.023, 0.0)]
        [TestCase(3.0, 0.005, -0.023, 0.0)]
        [TestCase(-4.0, -0.005, -0.023, -0.005)]
        [TestCase(-4.0, -0.005, 0.023, 0.005)]
        [TestCase(-4.0, 0.005, -0.023, 0.005)]
        [TestCase(-4.0, 0.005, 0.023, -0.005)]
        [TestCase(4.0, -0.005, -0.023, 0.005)]
        [TestCase(4.0, -0.005, 0.023, -0.005)]
        [TestCase(4.0, 0.005, -0.023, -0.005)]
        [TestCase(4.0, 0.005, 0.023, 0.005)]
        public void AutomatedAdjustmentController_LargeAzimuthErrorConvergesWithoutDrivingAltitudeAway(double azimuthError,
                                                                                                     double azimuthGain,
                                                                                                     double altitudeGain,
                                                                                                     double altitudeError) {
            var controller = new AutomatedAdjustmentController();
            var plant = new[,] {
                { azimuthGain, 0.0 },
                { 0.0, altitudeGain }
            };

            var result = RunClosedLoop(controller, plant, azimuthError, altitudeError, maxIterations: 250, targetErrorDegrees: 1.0 / 60.0);

            result.FinalErrorDegrees.Should().BeLessThan(1.0 / 60.0);
            result.MaximumAltitudeErrorDegrees.Should().BeLessThan(0.05, "altitude starts near zero and must not drift through repeated probes");
        }

        /// <summary>
        /// Replays the measured responses preceding the altitude probe at 20:55:41 in the
        /// 2026-10-18 UPAS log, when useful azimuth progress fell below the old 1% threshold.
        /// </summary>
        [Test]
        public void AutomatedAdjustmentController_KeepsCorrectingAzimuthForRecordedUpasResponses() {
            var controller = new AutomatedAdjustmentController();
            controller.UpdateObservation(-10495.0 / 3600.0, -400.0 / 3600.0);
            var responses = new[] {
                (X: 10.0, Y: 0.0, Probe: true, AzimuthArcseconds: -10874, AltitudeArcseconds: -417),
                (X: 0.0, Y: 1.0, Probe: true, AzimuthArcseconds: -10870, AltitudeArcseconds: -398),
                (X: -5.0, Y: 0.0, Probe: false, AzimuthArcseconds: -10844, AltitudeArcseconds: -381),
                (X: -5.0, Y: 0.0, Probe: false, AzimuthArcseconds: -10774, AltitudeArcseconds: -371),
                (X: -5.0, Y: 0.0, Probe: false, AzimuthArcseconds: -10747, AltitudeArcseconds: -380),
                (X: -5.0, Y: 0.0, Probe: false, AzimuthArcseconds: -10737, AltitudeArcseconds: -376),
                (X: -5.0, Y: 0.0, Probe: false, AzimuthArcseconds: -10725, AltitudeArcseconds: -374)
            };

            foreach (var response in responses) {
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(response.X, response.Y, response.Probe, "Recorded UPAS move"));
                controller.UpdateObservation(response.AzimuthArcseconds / 3600.0, response.AltitudeArcseconds / 3600.0);
            }

            var plan = controller.CreatePlan();

            plan.IsProbe.Should().BeFalse();
            plan.XMagnitude.Should().BeLessThan(0, "the logged negative X moves were correcting azimuth");
        }

        [Test]
        public void AutomatedAdjustmentController_UsesBothAxesAtUpasResponseScale() {
            var controller = new AutomatedAdjustmentController();
            controller.UpdateObservation(0.15, 0.1);
            controller.NoteSuccessfulExecution(controller.CreatePlan());
            controller.UpdateObservation(0.1, 0.1);
            controller.NoteSuccessfulExecution(controller.CreatePlan());
            controller.UpdateObservation(0.1, 0.123);

            var plan = controller.CreatePlan();

            plan.IsProbe.Should().BeFalse();
            plan.XMagnitude.Should().BeGreaterThan(0);
            plan.YMagnitude.Should().BeLessThan(0);
            var nextAzimuth = 0.1 - 0.005 * plan.XMagnitude;
            var nextAltitude = 0.123 + 0.023 * plan.YMagnitude;
            Math.Sqrt(nextAzimuth * nextAzimuth + nextAltitude * nextAltitude).Should().BeLessThan(0.1);
        }

        [TestCase(5.0, 1.0, true)]
        [TestCase(-5.0, -1.0, true)]
        [TestCase(5.0, 0.1, false)]
        [TestCase(-5.0, -0.1, false)]
        public void AutomatedAdjustmentController_ReprobesTheLessObservedAxisInProbeUnits(double xMove, double yMove, bool probeX) {
            var controller = new AutomatedAdjustmentController();
            var azimuthError = 4.0;
            var altitudeError = 4.0;
            controller.UpdateObservation(azimuthError, altitudeError);

            for (var i = 0; i < 12; i++) {
                controller.NoteSuccessfulExecution(new AutomatedAdjustmentPlan(xMove, yMove, false, "Two-axis correction"));
                azimuthError -= Math.Abs(xMove) * 0.005;
                altitudeError -= Math.Abs(yMove) * 0.023;
                controller.UpdateObservation(azimuthError, altitudeError);
            }

            controller.HasResponseModel.Should().BeFalse("the recent moves all have the same X/Y ratio");
            var plan = controller.CreatePlan();

            plan.IsProbe.Should().BeTrue();
            plan.XMagnitude.Should().Be(probeX ? 10.0 : 0.0);
            plan.YMagnitude.Should().Be(probeX ? 0.0 : 1.0);
        }

        [TestCase(0.0, 0.0)]
        [TestCase(0.00001, -0.00001)]
        public void AutomatedAdjustmentController_HoldsStillWhenLearnedCorrectionsAreBelowTheMoveDeadband(double azimuthError, double altitudeError) {
            var controller = new AutomatedAdjustmentController();
            controller.UpdateObservation(0.15, 0.1);
            controller.NoteSuccessfulExecution(controller.CreatePlan());
            controller.UpdateObservation(0.1, 0.1);
            controller.NoteSuccessfulExecution(controller.CreatePlan());
            controller.UpdateObservation(0.1, 0.123);
            controller.UpdateObservation(azimuthError, altitudeError);

            var plan = controller.CreatePlan();

            plan.HasMovement.Should().BeFalse("a fresh probe would disturb an already aligned mount");
        }

        [TestCase(-1.0)]
        [TestCase(1.0)]
        public void AutomatedAdjustmentController_UsesAnImprovingFullStepWhenDampingFallsBelowTheMoveDeadband(double direction) {
            var controller = new AutomatedAdjustmentController();
            controller.UpdateObservation(0.9, 0.6);
            controller.NoteSuccessfulExecution(controller.CreatePlan());
            controller.UpdateObservation(-0.9, 1.0);
            controller.NoteSuccessfulExecution(controller.CreatePlan());
            controller.UpdateObservation(-0.93, 0.89);
            var azimuthError = direction * 0.014533;
            var altitudeError = direction * -0.013912;
            controller.UpdateObservation(azimuthError, altitudeError);

            var plan = controller.CreatePlan();

            plan.HasMovement.Should().BeTrue();
            plan.IsProbe.Should().BeFalse();
            var nextAzimuth = azimuthError - 0.18 * plan.XMagnitude - 0.03 * plan.YMagnitude;
            var nextAltitude = altitudeError + 0.04 * plan.XMagnitude - 0.11 * plan.YMagnitude;
            Math.Sqrt(nextAzimuth * nextAzimuth + nextAltitude * nextAltitude).Should().BeLessThan(0.02);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AutomatedAdjustmentController_ReprobesUnresponsiveAxisWithoutDrivingTheOtherAxisAway(bool unresponsiveX) {
            var controller = new AutomatedAdjustmentController();
            var azimuthError = unresponsiveX ? 4.0 : 0.0;
            var altitudeError = unresponsiveX ? 0.0 : 4.0;
            controller.UpdateObservation(azimuthError, altitudeError);
            controller.NoteSuccessfulExecution(controller.CreatePlan());
            azimuthError += unresponsiveX ? 0.0 : 0.05;
            controller.UpdateObservation(azimuthError, altitudeError);
            controller.NoteSuccessfulExecution(controller.CreatePlan());
            altitudeError += unresponsiveX ? 0.023 : 0.0;
            controller.UpdateObservation(azimuthError, altitudeError);

            for (var i = 0; i < 12; i++) {
                var plan = controller.CreatePlan();
                plan.HasMovement.Should().BeTrue("a probe with no observed response may still be taking up mechanical play");
                if (plan.IsProbe) {
                    plan.XMagnitude.Should().Be(unresponsiveX ? 10.0 : 0.0);
                    plan.YMagnitude.Should().Be(unresponsiveX ? 0.0 : 1.0);
                    return;
                }

                if (unresponsiveX) {
                    plan.XMagnitude.Should().Be(0.0);
                    plan.YMagnitude.Should().BeLessThan(0);
                    altitudeError += 0.023 * plan.YMagnitude;
                } else {
                    plan.YMagnitude.Should().Be(0.0);
                    plan.XMagnitude.Should().BeLessThan(0);
                    azimuthError += 0.005 * plan.XMagnitude;
                }
                controller.NoteSuccessfulExecution(plan);
                controller.UpdateObservation(azimuthError, altitudeError);
            }

            Assert.Fail("The controller did not retry the unresponsive axis.");
        }

        private static ClosedLoopResult RunClosedLoop(AutomatedAdjustmentController controller,
                                                      double[,] plant,
                                                      double initialAzimuthErrorDegrees,
                                                      double initialAltitudeErrorDegrees,
                                                      int maxIterations,
                                                      double targetErrorDegrees = 0.02) {
            var azimuthError = initialAzimuthErrorDegrees;
            var altitudeError = initialAltitudeErrorDegrees;
            var maximumAltitudeError = Math.Abs(altitudeError);

            controller.UpdateObservation(azimuthError, altitudeError);

            for (var iteration = 0; iteration < maxIterations; iteration++) {
                var plan = controller.CreatePlan();
                if (!plan.HasMovement) {
                    break;
                }

                Math.Abs(plan.XMagnitude).Should().BeLessThanOrEqualTo(plan.IsProbe ? 10.0 : 5.0);
                Math.Abs(plan.YMagnitude).Should().BeLessThanOrEqualTo(plan.IsProbe ? 1.0 : 5.0);
                azimuthError += plant[0, 0] * plan.XMagnitude + plant[0, 1] * plan.YMagnitude;
                altitudeError += plant[1, 0] * plan.XMagnitude + plant[1, 1] * plan.YMagnitude;
                maximumAltitudeError = Math.Max(maximumAltitudeError, Math.Abs(altitudeError));

                controller.NoteSuccessfulExecution(plan);
                controller.UpdateObservation(azimuthError, altitudeError);

                var totalError = Math.Sqrt(azimuthError * azimuthError + altitudeError * altitudeError);
                if (totalError < targetErrorDegrees) {
                    return new ClosedLoopResult(totalError, iteration + 1, maximumAltitudeError);
                }
            }

            return new ClosedLoopResult(Math.Sqrt(azimuthError * azimuthError + altitudeError * altitudeError), maxIterations, maximumAltitudeError);
        }

        private sealed class ClosedLoopResult {
            public ClosedLoopResult(double finalErrorDegrees, int iterations, double maximumAltitudeErrorDegrees) {
                FinalErrorDegrees = finalErrorDegrees;
                Iterations = iterations;
                MaximumAltitudeErrorDegrees = maximumAltitudeErrorDegrees;
            }

            public double FinalErrorDegrees { get; }
            public int Iterations { get; }
            public double MaximumAltitudeErrorDegrees { get; }
        }
    }
}
