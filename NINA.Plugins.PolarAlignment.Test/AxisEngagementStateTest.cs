using FluentAssertions;
using NINA.Plugins.PolarAlignment;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Which side of its play each axis is engaged on belongs to the mechanism, not to the
    /// object driving it. The alignment instruction builds a new system object on every
    /// Execute - one field session shows four "Found OAPA System on COM11" lines - while the
    /// hardware holds still, so state carried on the object made every run begin by asserting
    /// both axes were engaged positive. On an axis with tens of arcminutes of play that turns
    /// the first correction of the run into either a whole injected backlash or a whole lost
    /// one, which is the largest single error such a run makes.
    /// </summary>
    public class AxisEngagementStateTest {

        [SetUp]
        public void Reset() => AxisEngagementState.Reset();

        [Test]
        public void DefaultsToPositive_ForAnAxisNothingHasDrivenYet() {
            AxisEngagementState.Get("Any", Axis.XAxis).Should().Be(LastDirection.Positive);
        }

        [Test]
        public void RemembersPerAxis_AndPerSystem() {
            AxisEngagementState.Set("OAPA", Axis.XAxis, LastDirection.Negative);

            AxisEngagementState.Get("OAPA", Axis.XAxis).Should().Be(LastDirection.Negative);
            AxisEngagementState.Get("OAPA", Axis.YAxis).Should().Be(LastDirection.Positive, "axes are independent");
            AxisEngagementState.Get("Avalon", Axis.XAxis).Should().Be(LastDirection.Positive,
                "two controllers in one session must not inherit each other's engagement");
        }

        // ----- Through the real production path -----

        private sealed class FakeLink : ISerialLink {
            public readonly List<string> Writes = new();
            private readonly Queue<string> pending = new();
            public float X, Y;

            public bool IsOpen => true;

            public void WriteLine(string text) {
                Writes.Add(text);
                if (text == "?") {
                    pending.Enqueue($"<Idle|MPos:{X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},{Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},0.00|V:1.2.2|>");
                    pending.Enqueue("ok");
                } else {
                    // Execute jogs instantly so the wait loop completes.
                    if (text.StartsWith("$J=G91G21X")) { X += Steps(text, "X"); }
                    if (text.StartsWith("$J=G91G21Y")) { Y += Steps(text, "Y"); }
                    pending.Enqueue("ok");
                }
            }

            private static float Steps(string cmd, string axis) {
                var body = cmd.Substring(cmd.IndexOf(axis, StringComparison.Ordinal) + 1);
                return float.Parse(body.Substring(0, body.IndexOf('F')), System.Globalization.CultureInfo.InvariantCulture);
            }

            public string ReadLine() => pending.Dequeue();
            public void Dispose() { }
        }

        private sealed class TestableOapa : UniversalPolarAlignmentOAPA {
            public TestableOapa(ISerialLink link) : base(link) { }
        }

        private static TestableOapa Build(FakeLink link) {
            Properties.Settings.Default.OAPAXGearRatio = 10f;
            Properties.Settings.Default.OAPAYGearRatio = 10f;
            var oapa = new TestableOapa(link);
            oapa.XGearRatio = 10f;
            oapa.YGearRatio = 10f;
            return oapa;
        }

        [Test]
        public async Task ANewSystemObject_InheritsTheEngagementTheMechanismIsActuallyIn() {
            // The manual nudge that preceded the field failure: the panel drives the axis
            // negative, then the instruction starts and constructs its own system object.
            var link = new FakeLink();
            var panel = Build(link);
            await panel.MoveRelative(Axis.YAxis, 1000, -10f, CancellationToken.None);
            panel.YLastDirection.Should().Be(LastDirection.Negative);

            var freshRun = Build(new FakeLink());

            freshRun.YLastDirection.Should().Be(LastDirection.Negative,
                "the axis is still resting against the same side of its play");
        }
    }
}
