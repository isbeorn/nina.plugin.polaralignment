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
                            var reply = line == "?" ? "<Idle|MPos:0.00,0.00,0.00|V:1.3.0|>\r\nok\r\n" : "ok\r\n";
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
    }
}
