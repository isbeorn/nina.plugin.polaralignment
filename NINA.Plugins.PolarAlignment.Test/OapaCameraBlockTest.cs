using FluentAssertions;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Exercises the camera capture-block coordination of the OAPA self-calibration through
    /// the production command path: a busy camera must abort before any motion, and a free
    /// camera must be blocked for the whole run and released again even when it fails.
    /// </summary>
    public class OapaCameraBlockTest {

        private sealed class FakeCameraMediator : ICameraMediator {
            public bool Free = true;
            public readonly List<string> Events = new();

            public int RegisterCount => Events.FindAll(e => e == "register").Count;
            public int ReleaseCount => Events.FindAll(e => e == "release").Count;

            public bool IsFreeToCapture(ICameraConsumer cameraConsumer) => Free;
            public bool IsFreeToCapture(object cameraConsumer) => Free;
            public void RegisterCaptureBlock(ICameraConsumer cameraConsumer) => Events.Add("register");
            public void RegisterCaptureBlock(object cameraConsumer) => Events.Add("register");
            public void ReleaseCaptureBlock(ICameraConsumer cameraConsumer) => Events.Add("release");
            public void ReleaseCaptureBlock(object cameraConsumer) => Events.Add("release");

            // Everything below is unused plumbing required by the interface.
            public Task Capture(CaptureSequence sequence, CancellationToken token, IProgress<ApplicationStatus> progress) => throw new NotImplementedException();
            public IAsyncEnumerable<IExposureData> LiveView(CancellationToken token) => throw new NotImplementedException();
            public IAsyncEnumerable<IExposureData> LiveView(CaptureSequence sequence, CancellationToken token) => throw new NotImplementedException();
            public Task<IExposureData> Download(CancellationToken token) => throw new NotImplementedException();
            public void AbortExposure() => throw new NotImplementedException();
            public void SetReadoutMode(short mode) => throw new NotImplementedException();
            public void SetReadoutModeForNormalImages(short mode) => throw new NotImplementedException();
            public void SetBinning(short x, short y) => throw new NotImplementedException();
            public void SetDewHeater(bool onOff) => throw new NotImplementedException();
            public bool AtTargetTemp => throw new NotImplementedException();
            public double TargetTemp => throw new NotImplementedException();
            public Task<bool> CoolCamera(double temperature, TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) => throw new NotImplementedException();
            public Task<bool> WarmCamera(TimeSpan duration, IProgress<ApplicationStatus> progress, CancellationToken ct) => throw new NotImplementedException();
            public void SetUSBLimit(int usbLimit) => throw new NotImplementedException();
            public void RegisterConsumer(ICameraConsumer consumer) => throw new NotImplementedException();
            public void RemoveConsumer(ICameraConsumer consumer) => throw new NotImplementedException();
            public Task<IList<string>> Rescan() => throw new NotImplementedException();
            public Task<bool> Connect() => throw new NotImplementedException();
            public Task Disconnect() => throw new NotImplementedException();
            public void Broadcast(CameraInfo deviceInfo) => throw new NotImplementedException();
            public CameraInfo GetInfo() => throw new NotImplementedException();
            public string Action(string actionName, string actionParameters) => throw new NotImplementedException();
            public string SendCommandString(string command, bool raw) => throw new NotImplementedException();
            public bool SendCommandBool(string command, bool raw) => throw new NotImplementedException();
            public void SendCommandBlind(string command, bool raw) => throw new NotImplementedException();
            public IDevice GetDevice() => throw new NotImplementedException();
            public void RegisterHandler(ICameraVM handler) => throw new NotImplementedException();

            public event Func<object, EventArgs, Task> DownloadTimeout { add { } remove { } }
            public event Func<object, EventArgs, Task> Connected { add { } remove { } }
            public event Func<object, EventArgs, Task> Disconnected { add { } remove { } }
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
            public LastDirection XLastDirection => LastDirection.Positive;
            public LastDirection YLastDirection => LastDirection.Positive;
            public LastDirection ZLastDirection => LastDirection.Positive;

            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) {
                RelativeMoves.Add((axis, position));
                return Task.CompletedTask;
            }

            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) {
                AbsoluteMoves.Add((axis, position));
                return Task.CompletedTask;
            }

            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }
        }

        private static (UniversalPolarAlignmentOAPAVM vm, FakeSystem system, FakeCameraMediator camera) Vm() {
            var camera = new FakeCameraMediator();
            var vm = new UniversalPolarAlignmentOAPAVM(null, null, null, null, camera);
            var system = new FakeSystem();
            vm.upa = system;
            return (vm, system, camera);
        }

        [Test]
        public async Task Calibration_CameraBusy_AbortsBeforeAnyMotion() {
            var (vm, system, camera) = Vm();
            camera.Free = false;

            await vm.CalibrateGearRatios(CancellationToken.None);

            system.RelativeMoves.Should().BeEmpty();
            system.AbsoluteMoves.Should().BeEmpty();
            camera.RegisterCount.Should().Be(0, "a refused calibration must not block the camera");
            vm.CalibrationStatus.Should().Be("Camera busy - calibration not started");
            vm.CalibrationRunning.Should().BeFalse();
        }

        [Test]
        public async Task Calibration_RegistersAndReleasesCaptureBlock_EvenOnFailure() {
            // The null-mediator plate-solve sampler fails at the baseline solve, so this run
            // takes the failure path: the capture block must still be released exactly once.
            var (vm, _, camera) = Vm();

            await vm.CalibrateGearRatios(CancellationToken.None);

            camera.Events.Should().Equal("register", "release");
            vm.CalibrationStatus.Should().StartWith("Failed");
        }

        [Test]
        public void CanCalibrate_RequiresFreeCamera() {
            var (vm, _, camera) = Vm();
            vm.Connected = true;

            camera.Free = false;
            vm.CanCalibrate().Should().BeFalse("a busy camera must disable the calibrate command");

            camera.Free = true;
            vm.CanCalibrate().Should().BeTrue();
        }
    }
}
