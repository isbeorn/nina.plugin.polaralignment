using FluentAssertions;
using NINA.Plugins.PolarAlignment.Instructions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// "Controller aligns": TPPA measures, the plugin forwards every reading, the controller
    /// moves. The plugin side is only the forwarding, the exclusion with TPPA's own automated
    /// adjustments, and the parameters pushed on connect.
    /// </summary>
    [NonParallelizable]
    public class OapaControllerAlignsTest {

        private sealed class FakeController : IPolarAlignmentSystem, IOapaAlignmentController {
            public readonly List<(double az, double alt)> Forwarded = new();
            public bool RunsAlignment { get; set; } = true;
            public bool Connected { get; set; } = true;
            public string Status => "Idle";
            public float XPosition1 => 0;
            public float YPosition1 => 0;
            public float ZPosition1 => 0;
            public float XGearRatio { get; set; } = 1;
            public float YGearRatio { get; set; } = 1;
            public float ZGearRatio { get; set; } = 1;
            public LastDirection XLastDirection => LastDirection.Positive;
            public LastDirection YLastDirection => LastDirection.Positive;
            public LastDirection ZLastDirection => LastDirection.Positive;
            public readonly List<(Axis axis, float position)> RelativeMoves = new();

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
                RelativeMoves.Add((axis, position));
                return Task.CompletedTask;
            }
            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) => Task.CompletedTask;
            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }

            public string ForwardError(double azimuthArcmin, double altitudeArcmin) {
                Forwarded.Add((azimuthArcmin, altitudeArcmin));
                return "ok";
            }

            public Queue<string> Statuses = new();

            public string AlignmentStatus() => Statuses.Count > 0 ? Statuses.Dequeue()
                : "<L|phase:moving_x|outcome:none|source:nina|moves:3|az:-2.10|alt:0.40|plan:-1.60,0.00|reason:Continuing corrections.|>";

            public int CalibrationRequests;
            public string Calibration = "<K|state:done|x:15.02,+1|y:14.98,-1|xplay:6.10,5.90,U|yplay:0.40,0.60,F|reason:Calibration complete|>";

            public string RequestCalibration() {
                CalibrationRequests++;
                return "ok";
            }

            public Queue<string> CalibrationStatuses = new();

            public string ControllerCalibrationStatus() => CalibrationStatuses.Count > 0 ? CalibrationStatuses.Dequeue() : Calibration;

            public int CalibrationOnlyRequests;
            public int CalibrationStops;

            public string RequestCalibrationOnly() {
                CalibrationOnlyRequests++;
                return "ok";
            }

            public string StopCalibration() {
                CalibrationStops++;
                return "ok";
            }
        }

        /// <summary>Frames whose field moves as the test says; or a solver that fails, or one that waits for the sky.</summary>
        private sealed class FakeSolver : IOapaCalibrationSolver {
            public readonly Queue<CalibrationSolveSample> Frames = new();
            public Exception Failure;
            public bool Hang;
            public readonly TaskCompletionSource Capturing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Captures;

            public async Task<CalibrationSolveSample> CaptureAndSolve(CancellationToken token) {
                Captures++;
                if (Failure != null) { throw Failure; }
                if (Hang) {
                    Capturing.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return Frames.Count > 1 ? Frames.Dequeue() : Frames.Peek();
            }
        }

        // Field due north at 45 deg altitude: the altitude projection cos(azimuth) is 1, azimuth is 1:1.
        private static CalibrationSolveSample Field(double azimuthDegrees, double altitudeDegrees) =>
            new CalibrationSolveSample(0, 0, altitudeDegrees, azimuthDegrees);

        private bool savedControllerAligns;
        private bool savedAutomated;
        private float savedX, savedY;
        private string savedXSource, savedYSource;
        private float savedXPlay, savedXPlayNeg, savedYPlay, savedYPlayNeg;
        private string savedXFactorMode, savedYFactorMode;
        private float savedXMech, savedYMech;
        private int savedXMotor, savedYMotor, savedXMicro;
        private string savedXMode, savedYMode, savedXPlaySource, savedYPlaySource;

        [SetUp]
        public void SaveSettings() {
            savedControllerAligns = Properties.Settings.Default.OAPAControllerAligns;
            savedAutomated = Properties.Settings.Default.DoAutomatedAdjustments;
            savedX = Properties.Settings.Default.OAPAXGearRatio;
            savedY = Properties.Settings.Default.OAPAYGearRatio;
            savedXSource = Properties.Settings.Default.OAPAXGearRatioSource;
            savedYSource = Properties.Settings.Default.OAPAYGearRatioSource;
            var d = Properties.Settings.Default;
            (savedXPlay, savedXPlayNeg, savedYPlay, savedYPlayNeg) = (d.OAPAXBacklashCompensation, d.OAPAXBacklashCompensationNegative, d.OAPAYBacklashCompensation, d.OAPAYBacklashCompensationNegative);
            (savedXMode, savedYMode, savedXPlaySource, savedYPlaySource) = (d.OAPAXBacklashMode, d.OAPAYBacklashMode, d.OAPAXBacklashSource, d.OAPAYBacklashSource);
            (savedXFactorMode, savedYFactorMode, savedXMech, savedYMech) = (d.OAPAXFactorMode, d.OAPAYFactorMode, d.OAPAXMechanicalRatio, d.OAPAYMechanicalRatio);
            (savedXMotor, savedYMotor, savedXMicro) = (d.OAPAXMotorStepsPerRev, d.OAPAYMotorStepsPerRev, d.OAPAXMicrosteps);
        }

        [TearDown]
        public void RestoreSettings() {
            Properties.Settings.Default.OAPAControllerAligns = savedControllerAligns;
            Properties.Settings.Default.DoAutomatedAdjustments = savedAutomated;
            Properties.Settings.Default.OAPAXGearRatio = savedX;
            Properties.Settings.Default.OAPAYGearRatio = savedY;
            Properties.Settings.Default.OAPAXGearRatioSource = savedXSource;
            Properties.Settings.Default.OAPAYGearRatioSource = savedYSource;
            var d = Properties.Settings.Default;
            (d.OAPAXBacklashCompensation, d.OAPAXBacklashCompensationNegative, d.OAPAYBacklashCompensation, d.OAPAYBacklashCompensationNegative) = (savedXPlay, savedXPlayNeg, savedYPlay, savedYPlayNeg);
            (d.OAPAXBacklashMode, d.OAPAYBacklashMode, d.OAPAXBacklashSource, d.OAPAYBacklashSource) = (savedXMode, savedYMode, savedXPlaySource, savedYPlaySource);
            (d.OAPAXFactorMode, d.OAPAYFactorMode, d.OAPAXMechanicalRatio, d.OAPAYMechanicalRatio) = (savedXFactorMode, savedYFactorMode, savedXMech, savedYMech);
            (d.OAPAXMotorStepsPerRev, d.OAPAYMotorStepsPerRev, d.OAPAXMicrosteps) = (savedXMotor, savedYMotor, savedXMicro);
        }

        [Test]
        public void TheGearRatioGivesStepsPerArcminute() {
            // 200 full steps x 16 microsteps x 100:1 over 21600 arcmin per turn.
            UniversalPolarAlignmentOAPAVM.FactorFromGear(100f, 200, 16).Should().BeApproximately(14.8148f, 1e-3f);
            UniversalPolarAlignmentOAPAVM.FactorFromGear(144f, 400, 8).Should().BeApproximately(21.3333f, 1e-3f);
        }

        [Test]
        public void InGearMode_TheFactorFollowsTheRatio_AndIsMarkedAsSuch() {
            var vm = new OapaTestVm();
            Properties.Settings.Default.OAPAXMicrosteps = 16;
            vm.XMotorStepsPerRev = 200;
            vm.XMechanicalRatio = 100f;
            vm.XGearRatio.Should().NotBeApproximately(14.8148f, 1e-3f, "in steps-per-arcminute mode the ratio is only stored");

            vm.XFactorMode = UniversalPolarAlignmentOAPAVM.FactorModeGear;

            vm.XGearRatio.Should().BeApproximately(14.8148f, 1e-3f);
            vm.XGearRatioSource.Should().Be(OapaParameterSource.Gear);
            vm.XFactorEditable.Should().BeFalse("the factor is computed, not typed, in gear mode");

            vm.XMechanicalRatio = 200f;
            vm.XGearRatio.Should().BeApproximately(29.6296f, 1e-3f);
        }

        [Test]
        public void ACalibration_BringsTheAxesBackToMeasuredSteps() {
            var controller = new FakeController();
            var vm = new OapaTestVm { Hardware = controller };
            vm.ControllerAligns = true;
            vm.XMechanicalRatio = 100f;
            vm.XFactorMode = UniversalPolarAlignmentOAPAVM.FactorModeGear;
            controller.Statuses.Enqueue("<L|phase:calibrating|outcome:none|source:nina|moves:2|az:-|alt:-|plan:0.00,0.00|reason:X|>");
            controller.Statuses.Enqueue("<L|phase:waiting|outcome:none|source:nina|moves:0|az:-|alt:-|plan:0.00,0.00|reason:W|>");

            vm.OnAlignmentError(30, -20);
            vm.OnAlignmentError(30, -20);

            vm.XFactorMode.Should().Be(UniversalPolarAlignmentOAPAVM.FactorModeSteps);
            vm.XGearRatio.Should().BeApproximately(15.02f, 1e-4f);
            vm.XGearRatioSource.Should().Be(OapaParameterSource.Calibrated);
        }

        [Test]
        public void TheForwarderListensOnTheTopicTheAlignmentPublishes() {
            var published = new PolarAlignmentErrorMessage(Guid.NewGuid(), 0, 0, 0);
            OapaErrorForwarder.ErrorTopic.Should().Be(published.Topic);
        }

        [Test]
        public async Task TheForwarderHandsOnTheReadingInArcminutes_AzimuthFirst() {
            (double az, double alt)? received = null;
            var forwarder = new OapaErrorForwarder(null, (az, alt) => received = (az, alt));

            await forwarder.OnMessageReceived(new PolarAlignmentErrorMessage(Guid.NewGuid(), altitudeError: -0.1, azimuthError: 0.25, totalError: 0.27));

            received.Should().NotBeNull();
            received.Value.az.Should().BeApproximately(15.0, 1e-9);
            received.Value.alt.Should().BeApproximately(-6.0, 1e-9);
        }

        [Test]
        public async Task TheForwarderIgnoresOtherTopics() {
            var calls = 0;
            var forwarder = new OapaErrorForwarder(null, (_, _) => calls++);

            await forwarder.OnMessageReceived(new PolarAlignmentProgressMessage(Guid.NewGuid(), new NINA.Core.Model.ApplicationStatus()));

            calls.Should().Be(0);
        }

        [Test]
        public void TppaNeverDrivesAnOapaAxis_WhateverTheSharedSettingSays() {
            // The controller moves the axes; TPPA only measures. The shared setting belongs to
            // the other alignment system as well, so it is kept, but it cannot hand OAPA to TPPA.
            var vm = new OapaTestVm();

            vm.DoAutomatedAdjustments = true;

            vm.DoAutomatedAdjustments.Should().BeFalse();
            Properties.Settings.Default.DoAutomatedAdjustments.Should().BeTrue();
        }

        [Test]
        public void AReading_IsForwarded_AndTheControllerStatusShown_WhenTheControllerAligns() {
            var controller = new FakeController();
            var vm = new OapaTestVm { Hardware = controller };
            vm.ControllerAligns = true;

            vm.OnAlignmentError(15.0, -6.0);

            controller.Forwarded.Should().Equal((15.0, -6.0));
            vm.ControllerStatus.Should().Be("moving azimuth, 3 moves - Continuing corrections.");
        }

        [Test]
        public void NothingIsForwarded_WhenTheOptionIsOff_OrTheFirmwareCannotAlign_OrTheLinkIsDown() {
            var controller = new FakeController();
            var vm = new OapaTestVm { Hardware = controller };

            vm.ControllerAligns = false;
            vm.OnAlignmentError(15.0, -6.0);

            vm.ControllerAligns = true;
            controller.Connected = false;
            vm.OnAlignmentError(15.0, -6.0);

            controller.Connected = true;
            controller.RunsAlignment = false;
            vm.OnAlignmentError(15.0, -6.0);

            controller.Forwarded.Should().BeEmpty();
            vm.ControllerStatus.Should().Contain("1.3.0");
        }

        [Test]
        public void TheParametersPushedOnConnect_CarryFactorsBacklashToleranceAndMoveCap() {
            var settings = new Properties.Settings();
            settings.OAPAXGearRatio = 12.5f;
            settings.OAPAYGearRatio = 40f;
            settings.OAPAXGearRatioSource = "Calibrated";
            settings.OAPAYGearRatioSource = "Manual";
            settings.OAPAXBacklashMode = "Unidirectional";
            settings.OAPAXBacklashCompensation = 14.78f;
            settings.OAPAXBacklashCompensationNegative = 10.21f;
            settings.OAPAYBacklashMode = "Soft";
            settings.OAPAYBacklashCompensation = 4f;
            settings.OAPAYBacklashCompensationNegative = -1f;  // never set: same as positive
            settings.AlignmentTolerance = 0.75;
            settings.OAPAMoveCap = 90f;

            UniversalPolarAlignmentOAPA.ControllerParameterCommands(settings).Should().Equal(
                "$F=12.5,40",
                "$B=X,U,14.78,10.21",
                "$B=Y,S,4,4",
                "$T=0.75",
                "$M=90");
        }

        [Test]
        public void TheMoveCap_StaysWithinWhatTheControllerAccepts() {
            var settings = new Properties.Settings();
            settings.OAPAMoveCap = 500f;
            UniversalPolarAlignmentOAPA.ControllerParameterCommands(settings).Should().Contain("$M=120");

            var vm = new OapaTestVm();
            vm.MoveCap = 0.2f;
            vm.MoveCap.Should().Be(1f);
            vm.MoveCap = 250f;
            vm.MoveCap.Should().Be(120f);
        }

        [Test]
        public void FactoryDefaultFactors_AreNotPushed_SoTheControllerCalibratesItself() {
            var settings = new Properties.Settings();
            settings.OAPAXGearRatioSource = "Default";
            settings.OAPAYGearRatioSource = "Calibrated";

            UniversalPolarAlignmentOAPA.ControllerParameterCommands(settings)
                .Should().NotContain(command => command.StartsWith("$F="));
        }

        [Test]
        public void ACalibrationTheControllerFinished_IsKeptAsTheCalibratedFactors() {
            var controller = new FakeController();
            var vm = new OapaTestVm { Hardware = controller };
            vm.ControllerAligns = true;
            controller.Statuses.Enqueue("<L|phase:calibrating|outcome:none|source:nina|moves:2|az:30.00|alt:-20.00|plan:0.00,0.00|reason:X: probe 2, 600 steps|>");
            controller.Statuses.Enqueue("<L|phase:waiting|outcome:none|source:nina|moves:0|az:30.00|alt:-20.00|plan:0.00,0.00|reason:Waiting for an error measurement|>");

            var before = vm.XGearRatio;
            vm.OnAlignmentError(30, -20);
            vm.XGearRatio.Should().Be(before, "nothing is taken while the controller is still calibrating");
            vm.OnAlignmentError(30, -20);

            vm.XGearRatio.Should().BeApproximately(15.02f, 1e-4f);
            vm.YGearRatio.Should().BeApproximately(14.98f, 1e-4f);
            Properties.Settings.Default.OAPAXGearRatioSource.Should().Be("Calibrated");

            // The play is kept as the mean of the two directions, in both, with the mode.
            vm.XBacklashCompensation.Should().BeApproximately(6.0f, 1e-4f);
            vm.XBacklashCompensationNegative.Should().BeApproximately(6.0f, 1e-4f);
            vm.XBacklashMode.Should().Be(OapaBacklashMode.Unidirectional);
            vm.YBacklashCompensation.Should().BeApproximately(0.5f, 1e-4f);
            vm.YBacklashMode.Should().Be(OapaBacklashMode.Full);
            Properties.Settings.Default.OAPAXBacklashSource.Should().Be("Calibrated");
        }

        [TestCase("<K|state:done|x:15.02,+1|y:14.98,-1|xplay:-|yplay:0.40,0.60,F|reason:c|>", 'X')]
        [TestCase("<K|state:done|x:15.02,+1|y:14.98,-1|xplay:1,2|yplay:-|reason:c|>", 'X')]
        [TestCase("<K|state:done|x:15.02,+1|y:14.98,-1|xplay:1,2,Q|yplay:-|reason:c|>", 'X')]
        [TestCase("<K|state:failed|x:15.02,+1|y:-|xplay:1,2,F|yplay:-|reason:c|>", 'X')]
        public void AnUnmeasuredOrUnreadablePlay_GivesNothing(string line, char axis) {
            OapaControllerStatus.CalibratedPlay(line, axis).Should().BeNull();
        }

        [Test]
        public void AFailedControllerCalibration_ChangesNoFactor() {
            var controller = new FakeController { Calibration = "<K|state:failed|x:15.02,+1|y:-|reason:axis Y does not respond|>" };
            var vm = new OapaTestVm { Hardware = controller };
            vm.ControllerAligns = true;
            var before = vm.XGearRatio;
            controller.Statuses.Enqueue("<L|phase:calibrating|outcome:none|source:nina|moves:1|az:-|alt:-|plan:0.00,0.00|reason:Y: probe 7|>");
            controller.Statuses.Enqueue("<L|phase:idle|outcome:none|source:none|moves:0|az:-|alt:-|plan:0.00,0.00|reason:|>");

            vm.OnAlignmentError(30, -20);
            vm.OnAlignmentError(30, -20);

            vm.XGearRatio.Should().Be(before);
        }

        [TestCase("<K|state:done|x:15.02,+1|y:14.98,-1|reason:Calibration complete|>", 15.02f, 14.98f)]
        public void TheCalibrationLine_GivesBothFactors(string line, float x, float y) {
            OapaControllerStatus.CalibratedFactors(line).Should().Be((x, y));
        }

        [TestCase("<K|state:failed|x:15.02,+1|y:-|reason:axis Y does not respond|>")]
        [TestCase("<K|state:idle|x:-|y:-|reason:|>")]
        [TestCase("ok")]
        [TestCase("")]
        public void AnUnfinishedOrUnreadableCalibration_GivesNoFactors(string line) {
            OapaControllerStatus.CalibratedFactors(line).Should().BeNull();
        }

        [TestCase("up", Axis.YAxis, 5f)]
        [TestCase("down", Axis.YAxis, -5f)]
        [TestCase("right", Axis.XAxis, 5f)]
        [TestCase("left", Axis.XAxis, -5f)]
        public void EachArrow_MovesItsAxisByTheStep(string direction, Axis axis, float arcmin) {
            UniversalPolarAlignmentOAPAVM.JogMove(direction, 5f).Should().Be((axis, arcmin));
        }

        [Test]
        public void AnUnknownArrow_MovesNothing() {
            UniversalPolarAlignmentOAPAVM.JogMove("sideways", 5f).Should().BeNull();
        }

        [Test]
        public async Task PressingAnArrow_NudgesTheAxisByTheChosenStep() {
            var controller = new FakeController();
            var vm = new OapaTestVm { Hardware = controller };
            vm.ReverseAltitude = false;
            vm.JogStep = 10f;

            await vm.JogCommand.ExecuteAsync("down");

            controller.RelativeMoves.Should().Equal((Axis.YAxis, -10f));
        }

        [Test]
        public async Task TheFeed_ForwardsTheFieldDisplacementSinceTheFirstFrame_UntilTheControllerIsDone() {
            var controller = new FakeController();
            controller.CalibrationStatuses.Enqueue("<K|state:done|x:1,+1|y:1,+1|xplay:-|yplay:-|reason:an old one|>");
            controller.CalibrationStatuses.Enqueue("<K|state:baseline|x:-|y:-|xplay:-|yplay:-|reason:AZ (X): reading where it starts|>");
            controller.CalibrationStatuses.Enqueue("<K|state:probing|x:-|y:-|xplay:-|yplay:-|reason:AZ (X): taking up the play, 200 steps|>");
            controller.CalibrationStatuses.Enqueue("<K|state:done|x:15.02,+1|y:14.98,-1|xplay:6.10,5.90,U|yplay:0.40,0.60,F|reason:Calibration complete|>");
            var solver = new FakeSolver();
            solver.Frames.Enqueue(Field(0.0, 45.0));
            solver.Frames.Enqueue(Field(0.0, 45.0));
            solver.Frames.Enqueue(Field(0.5, 45.0));        // azimuth moved 30'
            solver.Frames.Enqueue(Field(0.5, 45.25));       // then altitude 15'

            var line = await OapaCalibrationFeed.Run(solver, controller, null, CancellationToken.None);

            controller.CalibrationOnlyRequests.Should().Be(1, "these readings are field displacements, not a polar error");
            line.Should().Contain("state:done").And.Contain("15.02");
            controller.Forwarded.Should().HaveCount(4, "the old result before the run does not end it; its own end does");
            controller.Forwarded[2].az.Should().BeApproximately(30.0, 1e-6);
            controller.Forwarded[3].alt.Should().BeApproximately(15.0, 1e-3, "cos of the mean azimuth, 0.25 deg");
        }

        [Test]
        public async Task Calibrate_WithoutAPolarAlignment_RunsOnThePluginsOwnFrames_AndKeepsTheResult() {
            var controller = new FakeController();
            controller.CalibrationStatuses.Enqueue("<K|state:baseline|x:-|y:-|xplay:-|yplay:-|reason:b|>");
            var solver = new FakeSolver();
            solver.Frames.Enqueue(Field(0.0, 45.0));
            var vm = new OapaTestVm { Hardware = controller, Solver = solver };

            await vm.CalibrateOnControllerCommand.ExecuteAsync(null);

            controller.CalibrationOnlyRequests.Should().Be(1);
            controller.CalibrationRequests.Should().Be(0);
            vm.XGearRatio.Should().BeApproximately(15.02f, 1e-4f);
            vm.XBacklashCompensation.Should().BeApproximately(6.0f, 1e-4f);
            vm.CalibrationRunning.Should().BeFalse();
            vm.ControllerStatus.Should().StartWith("calibration done");
        }

        [Test]
        public async Task Calibrate_WhileTppaIsMeasuring_UsesTppasReadings_AndNoFrameIsTaken() {
            var controller = new FakeController();
            var solver = new FakeSolver();
            solver.Frames.Enqueue(Field(0.0, 45.0));
            var vm = new OapaTestVm { Hardware = controller, Solver = solver };
            vm.ControllerAligns = true;
            vm.OnAlignmentError(30, -20);

            await vm.CalibrateOnControllerCommand.ExecuteAsync(null);

            controller.CalibrationRequests.Should().Be(1);
            controller.CalibrationOnlyRequests.Should().Be(0);
            solver.Captures.Should().Be(0);
        }

        [Test]
        public async Task WhileTheControllerCalibrates_OnlyStopStaysAvailable_AndStopEndsIt() {
            var controller = new FakeController();
            var solver = new FakeSolver { Hang = true };
            solver.Frames.Enqueue(Field(0.0, 45.0));
            var vm = new OapaTestVm { Hardware = controller, Solver = solver };
            vm.Connected = true;

            var calibrating = vm.CalibrateOnControllerCommand.ExecuteAsync(null);
            await solver.Capturing.Task;

            vm.CalibrationRunning.Should().BeTrue();
            vm.IsNotMoving.Should().BeFalse("the axes are the controller's while it calibrates");
            vm.MoveXCommand.CanExecute(null).Should().BeFalse();
            vm.JogCommand.CanExecute("up").Should().BeFalse();
            vm.SetHomeCommand.CanExecute(null).Should().BeFalse();
            vm.StopMotionCommand.CanExecute(null).Should().BeTrue("STOP is the one control a running calibration must leave");

            vm.StopMotionCommand.Execute(null);
            await calibrating;

            controller.CalibrationStops.Should().Be(1, "the controller's calibration is ended too");
            vm.ControllerStatus.Should().Be("calibration stopped");
            vm.CalibrationRunning.Should().BeFalse();
            vm.IsNotMoving.Should().BeTrue();
        }

        [Test]
        public async Task AFrameThatCannotBeSolved_StopsTheControllersCalibration() {
            var controller = new FakeController();
            var solver = new FakeSolver { Failure = new InvalidOperationException("plate solve failed") };
            var vm = new OapaTestVm { Hardware = controller, Solver = solver };

            await vm.CalibrateOnControllerCommand.ExecuteAsync(null);

            controller.CalibrationStops.Should().Be(1);
            vm.ControllerStatus.Should().Contain("plate solve failed");
            vm.CalibrationRunning.Should().BeFalse();
        }

        [Test]
        public void Calibrate_IsOff_WhenTheControllerCannotRunIt_OrIsNotConnected() {
            var controller = new FakeController { RunsAlignment = false };
            var vm = new OapaTestVm { Hardware = controller, Solver = new FakeSolver() };
            vm.Connected = true;

            vm.CalibrateOnControllerCommand.CanExecute(null).Should().BeFalse("firmware before 1.3.0 has no calibration to run");

            controller.RunsAlignment = true;
            vm.CalibrateOnControllerCommand.CanExecute(null).Should().BeTrue();

            vm.Connected = false;
            vm.CalibrateOnControllerCommand.CanExecute(null).Should().BeFalse();
        }

        [Test]
        public async Task ARequestedCalibration_IsNotTakenFromTheControllerBeforeItHasRun() {
            // The controller starts a calibration on the reading after the request, and not at
            // all while its alignment is still running. Until it reports calibrating, the result
            // it holds is an earlier one, and taking it would replace the factor now in force.
            var controller = new FakeController();
            var vm = new OapaTestVm { Hardware = controller };
            vm.ControllerAligns = true;
            vm.Connected = true;
            vm.OnAlignmentError(30, -20);                      // TPPA is measuring
            vm.XGearRatio = 20f;                               // typed in after an earlier calibration

            await vm.CalibrateOnControllerCommand.ExecuteAsync(null);
            controller.Statuses.Enqueue("<L|phase:moving_x|outcome:none|source:nina|moves:4|az:-2.10|alt:0.40|plan:-1.60,0.00|reason:Continuing corrections.|>");
            vm.OnAlignmentError(28, -19);                      // still aligning; $K? has the earlier 15.02

            vm.XGearRatio.Should().BeApproximately(20f, 1e-4f);
            vm.XGearRatioSource.Should().Be(OapaParameterSource.Manual);
        }

        [Test]
        public async Task AControllerThatNeverStartsTheCalibration_EndsTheFeed_WithinAFewFrames() {
            // The controller starts on the reading after the request, so by the second reading it
            // reports a running state. One that stays idle has not taken the request (busy with
            // another source, or simulating); capturing on for 400 frames would look like a
            // calibration while nothing moves.
            var controller = new FakeController { Calibration = "<K|state:idle|x:-|y:-|xplay:-|yplay:-|reason:|>" };
            var solver = new FakeSolver();
            solver.Frames.Enqueue(Field(0.0, 45.0));

            Func<Task> run = () => OapaCalibrationFeed.Run(solver, controller, null, CancellationToken.None);

            await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*did not start*");
            solver.Captures.Should().BeLessThanOrEqualTo(3);
        }

        [TestCase("1.3.0", true)]
        [TestCase("1.10.0", true)]
        [TestCase("1.2.3", false)]
        [TestCase("1.2.2", false)]
        [TestCase(null, false)]
        [TestCase("", false)]
        public void OnlyFirmware130AndLaterRunsTheAlignment(string version, bool expected) {
            UniversalPolarAlignmentOAPA.FirmwareAtLeast(version, "1.3.0").Should().Be(expected);
        }

        [TestCase("<L|phase:waiting|outcome:none|source:nina|moves:0|az:-|alt:-|plan:0.00,0.00|reason:Waiting for an error measurement|>",
            "waiting for a reading, 0 moves - Waiting for an error measurement")]
        [TestCase("<L|phase:ended|outcome:finished|source:nina|moves:13|az:-|alt:-|plan:0.00,0.65|reason:Total error 0.70' below tolerance for 2 consecutive solves.|>",
            "ended: finished, 13 moves - Total error 0.70' below tolerance for 2 consecutive solves.")]
        [TestCase("ok", "unreadable status: ok")]
        [TestCase("", "")]
        public void TheStatusLineBecomesOneSentence(string line, string expected) {
            OapaControllerStatus.Describe(line).Should().Be(expected);
        }
    }
}
