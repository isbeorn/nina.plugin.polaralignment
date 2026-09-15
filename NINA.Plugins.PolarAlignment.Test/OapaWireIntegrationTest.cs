using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NINA.Plugins.PolarAlignment;
using NINA.Plugins.PolarAlignment.OAPA;
using NUnit.Framework;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// End-to-end tests of the wire layer through the ISerialLink seam: what the plugin
    /// actually writes to the port and how it reacts to what comes back. This is the layer
    /// where the driver-current grammar bug lived unseen for months - these tests exercise
    /// the real production path (UniversalPolarAlignmentBase + OAPA), not a fake system.
    /// </summary>
    public class OapaWireIntegrationTest {

        /// <summary>
        /// Scripted firmware double. Replies "ok" to every command; replies to "?" with a
        /// status frame built from a mutable position, so tests can simulate motion,
        /// stalls and external stops.
        /// </summary>
        private sealed class FakeFirmwareLink : ISerialLink {
            public readonly List<string> Writes = new();
            private readonly Queue<string> pending = new();

            public float X;
            public float Y;
            public string State = "Idle";
            public string Version = "1.2.2";
            /// <summary>Invoked after every non-status command line, e.g. to simulate motion.</summary>
            public Action<string> OnCommand = _ => { };
            /// <summary>Invoked before answering each "?" poll, e.g. to advance a simulated move.</summary>
            public Action OnPoll = () => { };

            public bool IsOpen => true;

            public void WriteLine(string text) {
                Writes.Add(text);
                if (text == "?") {
                    OnPoll();
                    pending.Enqueue($"<{State}|MPos:{X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},{Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},0.00|V:{Version}|>");
                    pending.Enqueue("ok");
                } else {
                    OnCommand(text);
                    pending.Enqueue("ok");
                }
            }

            public string ReadLine() {
                if (pending.Count == 0) { throw new TimeoutException("fake firmware has no pending reply"); }
                return pending.Dequeue();
            }

            public void Dispose() { }

            public IEnumerable<string> Commands => Writes.Where(w => w != "?");
        }

        private sealed class TestableOapa : UniversalPolarAlignmentOAPA {
            public TestableOapa(ISerialLink link) : base(link) { }
        }

        private static FakeFirmwareLink Link() => new();

        private static TestableOapa Connect(FakeFirmwareLink link) {
            // Known stored driver configuration and factors for deterministic expectations.
            Properties.Settings.Default.OAPAXRunCurrent = 600;
            Properties.Settings.Default.OAPAXHoldPercent = 40;
            Properties.Settings.Default.OAPAYRunCurrent = 1200;
            Properties.Settings.Default.OAPAYHoldPercent = 20;
            Properties.Settings.Default.OAPAXGearRatio = 10f;
            Properties.Settings.Default.OAPAYGearRatio = 10f;
            var oapa = new TestableOapa(link);
            oapa.XGearRatio = 10f;
            oapa.YGearRatio = 10f;
            return oapa;
        }

        [Test]
        public void Connect_ParsesTheStatusFrame_AndPushesTheStoredDriverConfiguration() {
            var link = Link();
            var oapa = Connect(link);

            oapa.FirmwareVersion.Should().Be("1.2.2");
            oapa.Status.Should().Be("Idle");
            // The push must use the firmware's type-first grammar, in a deterministic order.
            link.Commands.Should().ContainInOrder("CX600", "HX40", "CY1200", "HY20");
        }

        [Test]
        public void DriverConfigurationEdits_ReachTheWire_InTypeFirstGrammar() {
            var link = Link();
            var oapa = Connect(link);
            link.Writes.Clear();

            oapa.SetYRunCurrent(1000);
            oapa.SetXHoldPercent(35);

            link.Commands.Should().Equal("CY1000", "HX35");
        }

        [Test]
        public void RequestStop_WritesTheStopCommand() {
            var link = Link();
            var oapa = Connect(link);
            link.Writes.Clear();

            oapa.RequestStop();

            link.Commands.Should().Equal("!");
        }

        [Test]
        public async Task MoveRelative_WritesAnInvariantCultureJog_AndCompletesWhenThePositionArrives() {
            var link = Link();
            var oapa = Connect(link);
            // The fake firmware "executes" the jog instantly: position jumps to the target.
            link.OnCommand = cmd => {
                if (cmd.StartsWith("$J=G91G21X")) { link.X += 15f; link.State = "Idle"; }
            };
            link.Writes.Clear();

            // 1.5 axis units * ratio 10 = 15 steps; must be formatted with a decimal point
            // regardless of the machine's culture (the firmware parser knows only '.').
            await oapa.MoveRelative(Axis.XAxis, 1000, 1.5f, CancellationToken.None);

            link.Commands.First().Should().Be("$J=G91G21X15F1000");
        }

        [Test]
        public async Task MoveRelative_WithAFractionalStepCount_CompletesAtTheWholeStepTheFirmwareReaches() {
            // The firmware positions in whole steps and reports them as such: 1.53 axis units at
            // ratio 10 is 15.3 steps commanded and 15 reached. A completion check that waits for
            // 15.3 never finishes, and the move ends in a false "motor appears stuck".
            var link = Link();
            var oapa = Connect(link);
            link.OnCommand = cmd => {
                if (cmd.StartsWith("$J=G91G21X")) { link.X += 15f; link.State = "Idle"; }
            };
            link.Writes.Clear();

            Func<Task> act = () => oapa.MoveRelative(Axis.XAxis, 1000, 1.53f, CancellationToken.None);

            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task MoveRelative_ExitsGracefully_WhenTheFirmwareReportsIdleShortOfTheTarget() {
            var link = Link();
            var oapa = Connect(link);
            // The move starts but is stopped externally at 7 of 15 steps: firmware decelerates
            // and reports Idle. The wait loop must end without raising stuck/timeout errors.
            link.OnCommand = cmd => {
                if (cmd.StartsWith("$J=G91G21X")) { link.X += 7f; link.State = "Idle"; }
            };
            link.Writes.Clear();

            Func<Task> act = () => oapa.MoveRelative(Axis.XAxis, 1000, 1.5f, CancellationToken.None);

            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task MoveRelative_RaisesStuck_WhenThePositionNeverMoves() {
            var link = Link();
            var oapa = Connect(link);
            // Firmware keeps reporting Run at a frozen position: a real stall.
            link.OnCommand = cmd => {
                if (cmd.StartsWith("$J=G91G21X")) { link.State = "Run"; }
            };
            link.Writes.Clear();

            Func<Task> act = () => oapa.MoveRelative(Axis.XAxis, 1000, 1.5f, CancellationToken.None);

            await act.Should().ThrowAsync<TimeoutException>();
        }
    }
}
