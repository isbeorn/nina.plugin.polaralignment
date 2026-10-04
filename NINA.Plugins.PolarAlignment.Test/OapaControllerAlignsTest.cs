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
            public string FirmwareVersion { get; set; } = "1.3.0";
            public float MoveCapLimit { get; set; } = 120f;
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
                Events.Add(FormattableString.Invariant($"E:{azimuthArcmin},{altitudeArcmin}"));
                return "ok";
            }

            /// <summary>What reached the controller, in order: "T:tolerance" and "E:az,alt".</summary>
            public readonly List<string> Events = new();
            public readonly List<double> Tolerances = new();
            public int AlignmentStops;

            public void SyncTolerance(double arcmin) {
                Tolerances.Add(arcmin);
                Events.Add(FormattableString.Invariant($"T:{arcmin}"));
            }

            public string StopAlignment() {
                AlignmentStops++;
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

            /// <summary>The board's event log as $G= answers it; firmware before 1.3.1 answers "ok".</summary>
            public Func<uint, string> Board = _ => "ok";
            public int EventQueries;

            public string BoardEvent(uint afterSequence) {
                EventQueries++;
                return Board(afterSequence);
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
        private double savedTolerance;

        [SetUp]
        public void SaveSettings() {
            savedControllerAligns = Properties.Settings.Default.OAPAControllerAligns;
            savedAutomated = Properties.Settings.Default.DoAutomatedAdjustments;
            savedTolerance = Properties.Settings.Default.AlignmentTolerance;
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
            Properties.Settings.Default.AlignmentTolerance = savedTolerance;
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
            var published = new PolarAlignmentErrorMessage(Guid.NewGuid(), 0, 0, 0, 1);
            OapaErrorForwarder.ErrorTopic.Should().Be(published.Topic);
        }

        [Test]
        public async Task TheForwarderHandsOnTheReadingInArcminutes_AzimuthFirst() {
            (double az, double alt)? received = null;
            var forwarder = new OapaErrorForwarder(null, (az, alt, _) => received = (az, alt));

            await forwarder.OnMessageReceived(new PolarAlignmentErrorMessage(Guid.NewGuid(), altitudeError: -0.1, azimuthError: 0.25, totalError: 0.27, alignmentTolerance: 1));

            received.Should().NotBeNull();
            received.Value.az.Should().BeApproximately(15.0, 1e-9);
            received.Value.alt.Should().BeApproximately(-6.0, 1e-9);
        }

        [Test]
        public async Task TheForwarderIgnoresOtherTopics() {
            var calls = 0;
            var forwarder = new OapaErrorForwarder(null, (_, _, _) => calls++);

            await forwarder.OnMessageReceived(new PolarAlignmentProgressMessage(Guid.NewGuid(), new NINA.Core.Model.ApplicationStatus()));

            calls.Should().Be(0);
        }

        [Test]
        public async Task TheForwarderHandsOnTheInstructionsTolerance_OrNothingWhenTheMessageHasNone() {
            double? tolerance = -1;
            var forwarder = new OapaErrorForwarder(null, (_, _, t) => tolerance = t);

            await forwarder.OnMessageReceived(new PolarAlignmentErrorMessage(Guid.NewGuid(), -0.1, 0.25, 0.27, alignmentTolerance: 0.5));
            tolerance.Should().Be(0.5);

            await forwarder.OnMessageReceived(new BareErrorMessage());
            tolerance.Should().BeNull("a publisher without the field leaves the choice to the receiver");
        }

        /// <summary>An error message on the same topic that carries no tolerance.</summary>
        private sealed class BareErrorMessage : NINA.Plugin.Interfaces.IMessage {
            public Guid SenderId => Guid.Empty;
            public string Sender => "test";
            public DateTimeOffset SentAt => DateTimeOffset.UtcNow;
            public Guid MessageId => Guid.NewGuid();
            public DateTimeOffset? Expiration => null;
            public Guid? CorrelationId => null;
            public int Version => 1;
            public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();
            public string Topic => OapaErrorForwarder.ErrorTopic;
            public object Content => new { AzimuthError = 0.25, AltitudeError = -0.1 };
        }

        [Test]
        public void TheRunningInstructionsTolerance_ReachesTheControllerAheadOfTheReading() {
            // Every TPPA instruction carries its own tolerance: the controller has to finish at
            // the one the running instruction finishes at, not at the global default.
            Properties.Settings.Default.AlignmentTolerance = 1.0;
            var controller = new FakeController();
            var vm = new OapaTestVm { Hardware = controller };
            vm.ControllerAligns = true;

            vm.OnAlignmentError(30, -20, 0.5);
            vm.OnAlignmentError(28, -19);  // no tolerance in the message: the global default

            controller.Events.Should().Equal("T:0.5", "E:30,-20", "T:1", "E:28,-19");
        }

        [Test]
        public void TurningControllerAlignsOff_StopsTheControllersRun() {
            // The controller owns the moves: only stopping the readings would leave it finishing
            // the correction it is in - the other axis, the backlash legs.
            var controller = new FakeController();
            var vm = new OapaTestVm { Hardware = controller };
            vm.ControllerAligns = true;
            controller.AlignmentStops.Should().Be(0, "turning it on starts nothing by itself");

            vm.ControllerAligns = false;
            controller.AlignmentStops.Should().Be(1);

            vm.ControllerAligns = true;
            controller.RunsAlignment = false;
            vm.ControllerAligns = false;
            controller.AlignmentStops.Should().Be(1, "firmware before 1.3.0 runs no alignment to stop");
        }

        [Test]
        public void ThePanelShowsTheLastErrorTppaMeasured_EvenWithControllerAlignsOff() {
            // Hand moves are watched on these numbers, so they do not depend on the controller
            // aligning. Azimuth and altitude signed as measured; the total is TPPA's hypotenuse.
            var vm = new OapaTestVm { Hardware = new FakeController() };
            vm.ControllerAligns = false;
            vm.AzimuthErrorDisplay.Should().Be("\u2014", "nothing measured yet");

            vm.OnAlignmentError(2.1, -0.4);

            vm.AzimuthErrorDisplay.Should().Be("+2.10'");
            vm.AltitudeErrorDisplay.Should().Be("-0.40'");
            vm.TotalErrorDisplay.Should().Be("2.14'");
        }

        [Test]
        public void TheErrorReadout_ClearsAfter90SecondsWithoutAReading_AndSaysSo() {
            // What the panel shows is live: a value TPPA stopped updating is not left on screen.
            var now = new DateTime(2026, 9, 28, 1, 0, 0, DateTimeKind.Utc);
            var vm = new OapaTestVm { Clock = () => now };
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            vm.OnAlignmentError(1.0, 1.0);
            changed.Should().Contain(new[] { nameof(vm.AzimuthErrorDisplay), nameof(vm.AltitudeErrorDisplay), nameof(vm.TotalErrorDisplay) });

            now = now.AddSeconds(89);
            vm.TotalErrorDisplay.Should().Be("1.41'");
            now = now.AddSeconds(2);
            vm.AzimuthErrorDisplay.Should().Be("\u2014");
            vm.TotalErrorDisplay.Should().Be("\u2014");
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
        public void ThePanelSaysWhatControllerAlignsDoes_InTheStateItIsIn() {
            // The switch is drawn without its caption, so the panel says in words what it does.
            var vm = new OapaTestVm();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            vm.ControllerAligns = true;
            vm.ControllerAlignsMeaning.Should().Contain("moves the axes");
            vm.ControllerAligns = false;
            vm.ControllerAlignsMeaning.Should().Contain("only measures");
            changed.Should().Contain(nameof(vm.ControllerAlignsMeaning), "the sentence follows the switch");
        }

        [Test]
        public void TppasOwnBacklashAndAutomatedAdjustments_AreShownForUpasOnly() {
            // For OAPA the controller runs the alignment and compensates its own backlash: the
            // shared rows would show the OAPA play in "steps" and a switch that does nothing.
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PolarAlignment", "Options.xaml"))) {
                dir = dir.Parent;
            }
            var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "PolarAlignment", "Options.xaml"));
            var start = xaml.IndexOf("Grid.Row=\"21\"", StringComparison.Ordinal);
            var end = xaml.IndexOf("Grid.Row=\"23\"", start, StringComparison.Ordinal);
            var rows = xaml.Substring(start, end - start);

            rows.Should().Contain("Azimuth backlash compensation").And.Contain("Do automated adjustments?");
            rows.Should().NotContain("Binding IsSystemSelected");
            System.Text.RegularExpressions.Regex.Matches(rows, @"Visibility=""\{Binding IsUPASSelected").Count.Should().Be(5);
        }

        [Test]
        public void TheMoveCapSent_StaysWithinWhatTheControllerAccepts() {
            // The controller says how far it goes ($M?); firmware before 1.3.2 cannot say, and stops at 120'.
            var settings = new Properties.Settings();
            settings.OAPAMoveCap = 250f;
            UniversalPolarAlignmentOAPA.ControllerParameterCommands(settings, 180f).Should().Contain("$M=180");
            UniversalPolarAlignmentOAPA.ControllerParameterCommands(settings, 120f).Should().Contain("$M=120");
            settings.OAPAMoveCap = 150f;
            UniversalPolarAlignmentOAPA.ControllerParameterCommands(settings, 180f).Should().Contain("$M=150");
        }

        [TestCase("<M|max:180|>", 180f)]
        [TestCase("<M|max:240|>", 240f)]
        [TestCase("<M|max:999|>", 300f)]
        [TestCase("ok", 120f)]
        [TestCase("error", 120f)]
        [TestCase(null, 120f)]
        public void TheControllersMoveCapLimit_IsWhatItReports_Or120ForOlderFirmware(string reply, float expected) {
            UniversalPolarAlignmentOAPA.ParseMoveCapLimit(reply).Should().Be(expected);
        }

        [Test]
        public void Disconnected_TheMoveCapGoesUpTo300_AndAbove120ItAsksFirst() {
            var asked = new List<string>();
            var vm = new OapaTestVm { ConfirmLargeMoveCap = message => { asked.Add(message); return true; } };
            vm.MoveCap = 0.2f;
            vm.MoveCap.Should().Be(1f);
            vm.MoveCap = 100f;
            asked.Should().BeEmpty("120' and below needs no warning");
            vm.MoveCap = 250f;
            vm.MoveCap.Should().Be(250f);
            asked.Should().ContainSingle().Which.Should().Contain("250'");
            vm.MoveCap = 400f;
            vm.MoveCap.Should().Be(300f);
        }

        [Test]
        public void AMoveCapAbove120_NotConfirmed_KeepsTheOneBefore() {
            var vm = new OapaTestVm { ConfirmLargeMoveCap = _ => false };
            vm.MoveCap = 60f;
            vm.MoveCap = 150f;
            vm.MoveCap.Should().Be(60f);
        }

        [Test]
        public void Connected_TheMoveCapStopsAtTheControllersLimit_AndSaysSo() {
            var asked = new List<string>();
            var controller = new FakeController { MoveCapLimit = 180f, FirmwareVersion = "1.3.2" };
            var vm = new OapaTestVm { Hardware = controller, ConfirmLargeMoveCap = message => { asked.Add(message); return true; } };
            vm.MoveCap = 60f;

            vm.MoveCap = 250f;

            vm.MoveCap.Should().Be(180f);
            asked.Should().ContainSingle().Which.Should().Contain("180'", "the warning is about the cap that will apply");
            vm.ControllerStatus.Should().Contain("up to 180'");
        }

        [Test]
        public void ThePanelShowsTheFirmwareVersion() {
            var controller = new FakeController { FirmwareVersion = "1.3.2" };
            var vm = new OapaTestVm { Hardware = controller };
            vm.FirmwareVersionDisplay.Should().Be("Firmware 1.3.2");

            controller.Connected = false;
            vm.FirmwareVersionDisplay.Should().BeEmpty();
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
            controller.Tolerances.Should().BeEmpty("a calibration has no alignment tolerance to give");
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

            var calibrating = vm.CalibrateOnControllerCommand.ExecuteAsync(null);
            vm.OnAlignmentError(29, -20);
            await calibrating;

            controller.CalibrationRequests.Should().Be(1);
            controller.CalibrationOnlyRequests.Should().Be(0);
            solver.Captures.Should().Be(0);
        }

        [Test]
        public async Task Calibrate_RightAfterTppaStopped_FallsBackToThePluginsOwnFrames() {
            // valo_20260928 19:14: Calibrate 12 s after TPPA's last reading asked the controller to
            // calibrate on TPPA's readings, and none came: the panel waited until pressed again.
            var controller = new FakeController();
            controller.CalibrationStatuses.Enqueue("<K|state:baseline|x:-|y:-|xplay:-|yplay:-|reason:b|>");
            var solver = new FakeSolver();
            solver.Frames.Enqueue(Field(0.0, 45.0));
            var vm = new OapaTestVm { Hardware = controller, Solver = solver, TppaAnswerWindow = TimeSpan.FromMilliseconds(200) };
            vm.ControllerAligns = true;
            vm.OnAlignmentError(30, -20);

            await vm.CalibrateOnControllerCommand.ExecuteAsync(null);

            controller.CalibrationRequests.Should().Be(1);
            controller.CalibrationStops.Should().Be(1, "the request nothing feeds is withdrawn first");
            controller.CalibrationOnlyRequests.Should().Be(1);
            solver.Captures.Should().BeGreaterThan(0);
            vm.ControllerStatus.Should().StartWith("calibration done");
        }

        [Test]
        public void AfterEachForwardedReading_TheBoardsNewEventsAreRead() {
            var controller = new FakeController { Board = OapaBoardEventsTest.BoardWith("AZ (X) +0.28' (+4 steps)") };
            var vm = new OapaTestVm { Hardware = controller };
            vm.ControllerAligns = true;

            vm.OnAlignmentError(30, -20);

            controller.EventQueries.Should().Be(2, "the one event, then the answer that nothing is newer");
        }

        [Test]
        public async Task ACalibrationOnThePluginsOwnFrames_ReadsTheBoardsEventsAfterEachFrame() {
            var controller = new FakeController { Board = OapaBoardEventsTest.BoardWith() };
            controller.CalibrationStatuses.Enqueue("<K|state:baseline|x:-|y:-|xplay:-|yplay:-|reason:b|>");
            var solver = new FakeSolver();
            solver.Frames.Enqueue(Field(0.0, 45.0));
            var vm = new OapaTestVm { Hardware = controller, Solver = solver };

            await vm.CalibrateOnControllerCommand.ExecuteAsync(null);

            controller.EventQueries.Should().Be(solver.Captures);
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

            var calibrating = vm.CalibrateOnControllerCommand.ExecuteAsync(null);
            controller.Statuses.Enqueue("<L|phase:moving_x|outcome:none|source:nina|moves:4|az:-2.10|alt:0.40|plan:-1.60,0.00|reason:Continuing corrections.|>");
            vm.OnAlignmentError(28, -19);                      // still aligning; $K? has the earlier 15.02
            await calibrating;
            controller.CalibrationStops.Should().Be(0, "TPPA answered: the request stands");

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
