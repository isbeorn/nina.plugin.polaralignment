using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The shared serial base carries behaviour the OAPA driver opts into - reopening a dropped
    /// link, remembering which side of its play an axis rests on across system objects. A
    /// system that does not opt in, as the Avalon UPAS driver does not, must keep the behaviour
    /// it had before those existed.
    /// </summary>
    public partial class UniversalPolarAlignmentDefaultsTest {

        [SetUp]
        public void ForgetEngagement() => AxisEngagementState.Reset();

        /// <summary>Scripted controller that can drop dead like a yanked USB cable.</summary>
        private sealed class ScriptedLink : ISerialLink {
            private readonly Queue<string> pending = new();
            private bool dead;
            public bool KillOnNextPoll;
            public float Y;
            public int ReopenCalls { get; private set; }

            public bool IsOpen => !dead;

            public void WriteLine(string text) {
                if (dead) { throw new InvalidOperationException("The port is closed."); }
                if (text == "?") {
                    if (KillOnNextPoll) {
                        KillOnNextPoll = false;
                        dead = true;
                        throw new InvalidOperationException("The port is closed.");
                    }
                    pending.Enqueue($"<Idle|MPos:0.00,{Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},0.00|>");
                    pending.Enqueue("ok");
                } else {
                    if (text.StartsWith("$J=G91G21Y")) {
                        var body = text.Substring("$J=G91G21Y".Length);
                        Y += float.Parse(body.Substring(0, body.IndexOf('F')), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    pending.Enqueue("ok");
                }
            }

            public string ReadLine() => pending.Dequeue();

            public bool TryReopen() {
                ReopenCalls++;
                dead = false;
                return true;
            }

            public void Dispose() { }
        }

        /// <summary>A system that overrides none of the shared behaviour, like the Avalon driver.</summary>
        private sealed partial class DefaultSystem : UniversalPolarAlignmentBase {
            public DefaultSystem(ISerialLink link) : base(link) { }

            protected override string SystemName => "Default System";
            protected override int LinkReopenDelayMs => 0;
            public override float XGearRatio { get; set; } = 1;
            public override float YGearRatio { get; set; } = 1;
            protected override Regex GetStatusRegex() => StatusRegex();

            [GeneratedRegex(@"<(?<status>\w+)\|MPos:(?<x>[+-]?\d+(\.\d+)?),(?<y>[+-]?\d+(\.\d+)?),(?<z>[+-]?\d+(\.\d+)?)\|>")]
            private static partial Regex StatusRegex();
        }

        [Test]
        public async Task ADroppedLink_IsNotReopened_ForASystemThatDoesNotOptIn() {
            var link = new ScriptedLink();
            var system = new DefaultSystem(link);
            link.KillOnNextPoll = true;

            var act = () => system.MoveRelative(Axis.YAxis, 700, 10f, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>("the failure reaches the caller as it always did");
            link.ReopenCalls.Should().Be(0);
        }

        [Test]
        public async Task ANewSystemObject_StartsEngagedPositive_ForASystemThatDoesNotOptIn() {
            var first = new DefaultSystem(new ScriptedLink());
            await first.MoveRelative(Axis.YAxis, 700, -10f, CancellationToken.None);
            first.YLastDirection.Should().Be(LastDirection.Negative);

            var second = new DefaultSystem(new ScriptedLink());

            second.YLastDirection.Should().Be(LastDirection.Positive,
                "each new system object assumes positive, as before the engagement was remembered");
        }
    }
}
