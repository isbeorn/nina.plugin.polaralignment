using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The OAPA line protocol over WiFi, against a scripted controller on the loopback
    /// interface. The link has to look exactly like a serial port to everything above it:
    /// same lines, TimeoutException for silence, IOException for a dropped connection.
    /// </summary>
    public class OapaTcpLinkTest {

        /// <summary>
        /// A controller that speaks the 1.3.0 wire discipline: "?" gets the frame and "ok",
        /// every other line gets "ok", unless it is told to stay silent or to hang up.
        /// </summary>
        private sealed class FakeController : IDisposable {
            private readonly TcpListener listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource cts = new();
            public readonly ConcurrentQueue<string> Received = new();
            public volatile bool Silent;
            public volatile bool HangUpOnNextLine;
            public volatile string Version = "1.3.0";
            public readonly ConcurrentDictionary<string, string> Replies = new();
            public int Accepted;

            public FakeController() {
                listener.Start();
                _ = Task.Run(AcceptLoop);
            }

            public string Address => $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

            private async Task AcceptLoop() {
                while (!cts.IsCancellationRequested) {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(cts.Token); } catch { return; }
                    Interlocked.Increment(ref Accepted);
                    _ = Task.Run(() => Serve(client));
                }
            }

            private void Serve(TcpClient client) {
                using (client) {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    try {
                        string line;
                        while ((line = reader.ReadLine()) != null) {
                            Received.Enqueue(line);
                            if (HangUpOnNextLine) {
                                HangUpOnNextLine = false;
                                return;
                            }
                            if (Silent) { continue; }
                            var reply = line == "?" ? $"<Idle|MPos:0.00,0.00,0.00|V:{Version}|>\r\nok\r\n"
                                : Replies.TryGetValue(line, out var scripted) ? scripted + "\r\n" : "ok\r\n";
                            var bytes = Encoding.ASCII.GetBytes(reply);
                            stream.Write(bytes, 0, bytes.Length);
                        }
                    } catch (IOException) {
                    }
                }
            }

            public void Dispose() {
                cts.Cancel();
                listener.Stop();
            }
        }

        [Test]
        public void Address_DefaultsToPort2323_AndAcceptsHostColonPort() {
            OapaTcpLink.ParseAddress("192.168.1.50").Should().Be(("192.168.1.50", 2323));
            OapaTcpLink.ParseAddress(" oapa.local:4000 ").Should().Be(("oapa.local", 4000));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        [TestCase("192.168.1.50:")]
        [TestCase("192.168.1.50:abc")]
        [TestCase("192.168.1.50:70000")]
        [TestCase(":2323")]
        public void Address_Malformed_IsRefusedWithAReason(string address) {
            var act = () => OapaTcpLink.ParseAddress(address);
            act.Should().Throw<ArgumentException>();
        }

        [Test]
        public void StatusProbe_ReadsTheSameTwoLinesAsASerialPort() {
            using var controller = new FakeController();
            using var link = OapaTcpLink.Connect(controller.Address);

            link.WriteLine("?");

            // SerialPort.ReadLine with NewLine "\n" keeps the '\r' of the firmware's println.
            link.ReadLine().Should().Be("<Idle|MPos:0.00,0.00,0.00|V:1.3.0|>\r");
            link.ReadLine().Should().Be("ok\r");
            link.IsOpen.Should().BeTrue();
        }

        [Test]
        public void ASilentController_TimesOut_LikeASerialPort() {
            using var controller = new FakeController { Silent = true };
            using var link = OapaTcpLink.Connect(controller.Address);

            link.WriteLine("?");
            var act = () => link.ReadLine();

            act.Should().Throw<TimeoutException>();
            link.IsOpen.Should().BeTrue("silence is not a dropped connection");
        }

        [Test]
        public void AControllerThatHangsUp_IsALinkFailure_AndTryReopenConnectsAgain() {
            using var controller = new FakeController { HangUpOnNextLine = true };
            using var link = OapaTcpLink.Connect(controller.Address);

            link.WriteLine("?");
            var act = () => link.ReadLine();

            act.Should().Throw<IOException>("the base treats IOException as a link failure and tries to reopen");
            link.IsOpen.Should().BeFalse();

            link.TryReopen().Should().BeTrue();
            link.WriteLine("?");
            link.ReadLine().Should().StartWith("<Idle|");
            controller.Accepted.Should().Be(2);
        }

        [Test]
        public void NobodyListening_FailsTheConnect() {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var act = () => OapaTcpLink.Connect($"127.0.0.1:{port}");

            act.Should().Throw<Exception>();
        }

        [Test]
        public void ConnectOverWifi_ProbesAndPushesTheDriverSettings_ThroughTheSameWirePath() {
            using var controller = new FakeController();

            using var system = UniversalPolarAlignmentOAPA.ConnectOverWifi(controller.Address);

            system.Connected.Should().BeTrue();
            controller.Received.Should().Contain("?");
            controller.Received.Should().Contain(line => line.StartsWith("CX"), "the stored driver settings are pushed on every connection");
            controller.Received.Should().Contain(line => line.StartsWith("HY"));
        }

        [Test]
        public void TheTolerance_IsSentWhenItChanges_AndOnlyThen_AndAReadingIsOnlyAReading() {
            using var controller = new FakeController();
            using var system = UniversalPolarAlignmentOAPA.ConnectOverWifi(controller.Address);
            while (controller.Received.TryDequeue(out _)) { }

            system.SyncTolerance(1.0);
            system.SyncTolerance(1.0);
            system.SyncTolerance(0.5);
            system.SyncTolerance(0);  // an instruction without a tolerance: the controller keeps its own
            system.ForwardError(26, -18);

            controller.Received.Should().Equal("$T=1", "$T=0.5", "$E=26.000,-18.000");
        }

        [Test]
        [NonParallelizable]
        public void TurningControllerAlignsOff_SendsTheStopToTheController() {
            var d = Properties.Settings.Default;
            var saved = d.OAPAControllerAligns;
            try {
                d.OAPAControllerAligns = true;
                using var controller = new FakeController();
                using var system = UniversalPolarAlignmentOAPA.ConnectOverWifi(controller.Address);
                var vm = new OapaTestVm { Hardware = system };
                while (controller.Received.TryDequeue(out _)) { }

                vm.ControllerAligns = false;

                controller.Received.Should().Equal("$A=0");
            } finally {
                d.OAPAControllerAligns = saved;
            }
        }

        [Test]
        public void FirmwareOlderThan130_IsFlaggedWhenConnecting() {
            // The shared connection check warns below the minimum; 1.3.0 is where the
            // controller runs the alignment, so a 1.2.x board is named at connection instead
            // of when TPPA's first reading goes nowhere.
            using var controller = new FakeController();
            using var system = UniversalPolarAlignmentOAPA.ConnectOverWifi(controller.Address);

            system.GetType().GetProperty("MinimumFirmwareVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .GetValue(system).Should().Be(UniversalPolarAlignmentOAPA.AlignmentFirmwareVersion);
        }

        [Test]
        [NonParallelizable]
        public void OnConnect_TheControllersMoveCapLimitIsRead_AndTheCapSentStaysWithinIt() {
            var d = Properties.Settings.Default;
            var saved = d.OAPAMoveCap;
            try {
                d.OAPAMoveCap = 250f;
                using var controller = new FakeController { Version = "1.3.2" };
                controller.Replies["$M?"] = "<M|max:180|>";
                using (var system = UniversalPolarAlignmentOAPA.ConnectOverWifi(controller.Address)) {
                    system.MoveCapLimit.Should().Be(180f);
                    controller.Received.Should().Contain("$M=180");
                }

                using var older = new FakeController();   // 1.3.0: "$M?" is an unknown command, answered "ok"
                using var olderSystem = UniversalPolarAlignmentOAPA.ConnectOverWifi(older.Address);
                olderSystem.MoveCapLimit.Should().Be(120f);
                older.Received.Should().Contain("$M=120");
            } finally {
                d.OAPAMoveCap = saved;
            }
        }

        [TestCase("microsteps", "$F=30,15")]
        [TestCase("factor", "$F=20,15")]
        [TestCase("factorY", "$F=15,20")]
        [TestCase("gear", "$F=14.814815,15")]
        [TestCase("play", "$B=X,F,4,4")]
        [TestCase("mode", "$B=X,S,2,2")]
        [TestCase("cap", "$M=120")]
        [NonParallelizable]
        public void AValueChangedWhileConnected_ReachesTheController(string change, string expected) {
            // The controller aligns with the values it was given, not with the ones the panel
            // shows. Doubling the microsteps doubles the steps per arcminute: a controller left
            // on 15 would move every correction half as far as it computes.
            var d = Properties.Settings.Default;
            var saved = (d.OAPAXGearRatio, d.OAPAYGearRatio, d.OAPAXGearRatioSource, d.OAPAYGearRatioSource, d.OAPAXMicrosteps,
                d.OAPAXFactorMode, d.OAPAXMechanicalRatio, d.OAPAXMotorStepsPerRev,
                d.OAPAXBacklashCompensation, d.OAPAXBacklashCompensationNegative, d.OAPAXBacklashMode, d.OAPAMoveCap);
            try {
                d.OAPAMoveCap = 30f;
                (d.OAPAXGearRatio, d.OAPAYGearRatio) = (15f, 15f);
                (d.OAPAXGearRatioSource, d.OAPAYGearRatioSource) = (nameof(OapaParameterSource.Calibrated), nameof(OapaParameterSource.Calibrated));
                (d.OAPAXMicrosteps, d.OAPAXFactorMode, d.OAPAXMechanicalRatio, d.OAPAXMotorStepsPerRev) = (16, UniversalPolarAlignmentOAPAVM.FactorModeSteps, 100f, 200);
                (d.OAPAXBacklashCompensation, d.OAPAXBacklashCompensationNegative, d.OAPAXBacklashMode) = (2f, -1f, nameof(OapaBacklashMode.Full));
                using var controller = new FakeController();
                using var system = UniversalPolarAlignmentOAPA.ConnectOverWifi(controller.Address);
                var vm = new OapaTestVm { Hardware = system };
                while (controller.Received.TryDequeue(out _)) { }

                switch (change) {
                    case "microsteps": vm.XMicrosteps = 32; break;
                    case "factor": vm.XGearRatio = 20f; break;
                    case "factorY": vm.YGearRatio = 20f; break;
                    case "gear": vm.XFactorMode = UniversalPolarAlignmentOAPAVM.FactorModeGear; break;   // 200 x 16 x 100 / 21600
                    case "play": vm.XBacklashCompensation = 4f; break;
                    case "mode": vm.XBacklashMode = OapaBacklashMode.Soft; break;
                    case "cap": vm.MoveCap = 120f; break;
                }

                controller.Received.Should().Contain(expected);
            } finally {
                (d.OAPAXGearRatio, d.OAPAYGearRatio, d.OAPAXGearRatioSource, d.OAPAYGearRatioSource, d.OAPAXMicrosteps,
                    d.OAPAXFactorMode, d.OAPAXMechanicalRatio, d.OAPAXMotorStepsPerRev,
                    d.OAPAXBacklashCompensation, d.OAPAXBacklashCompensationNegative, d.OAPAXBacklashMode, d.OAPAMoveCap) = saved;
            }
        }
    }
}
