using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// Mid-session serial dropouts through the production wire path. Field case: the
    /// USB-serial adapter re-enumerated under the EMI of a stalling stepper, every
    /// subsequent transaction threw "The port is closed", and the session was dead
    /// until the user reconnected by hand. The link layer must try to reopen the port
    /// on a bounded schedule and only then give up.
    /// </summary>
    public class SerialLinkRecoveryTest {

        /// <summary>
        /// Scripted firmware link that can drop dead like a yanked USB cable: while
        /// dead, every write throws InvalidOperationException exactly like SerialPort.
        /// TryReopen revives it after a configurable number of attempts.
        /// </summary>
        private sealed class DroppableLink : ISerialLink {
            private readonly Queue<string> pending = new();
            private bool dead;
            private bool killOnNextTransaction;
            public float X, Y;
            public int ReopenCalls { get; private set; }
            public int ReviveOnAttempt { get; set; } = 1;

            public bool IsOpen => !dead;

            public void KillAfterCurrentCommand() => killOnNextTransaction = true;

            public void WriteLine(string text) {
                if (dead) {
                    throw new InvalidOperationException("The port is closed.");
                }
                if (killOnNextTransaction && text == "?") {
                    killOnNextTransaction = false;
                    dead = true;
                    throw new InvalidOperationException("The port is closed.");
                }
                if (text == "?") {
                    pending.Enqueue($"<Idle|MPos:{X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},{Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},0.00|V:1.2.2|>");
                    pending.Enqueue("ok");
                } else {
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

            public bool TryReopen() {
                if (!dead) {
                    return true;
                }
                ReopenCalls++;
                if (ReopenCalls >= ReviveOnAttempt) {
                    dead = false;
                    return true;
                }
                return false;
            }

            public void Dispose() { }
        }

        private sealed class TestableOapa : UniversalPolarAlignmentOAPA {
            public TestableOapa(ISerialLink link) : base(link) { }

            // Collapse the 1s/2s/3s reopen schedule so the dead-link test does not sleep.
            protected override int LinkReopenDelayMs => 0;
        }

        private static TestableOapa Build(DroppableLink link) {
            Properties.Settings.Default.OAPAXGearRatio = 10f;
            Properties.Settings.Default.OAPAYGearRatio = 10f;
            var oapa = new TestableOapa(link) {
                XGearRatio = 10f,
                YGearRatio = 10f
            };
            return oapa;
        }

        [Test]
        public async Task AMidMoveDropout_IsReopenedAndTheMoveCompletes() {
            // The link dies on the status poll right after the jog was accepted - the
            // exact moment of the field failure. One successful reopen must let the
            // same move finish instead of surfacing "The port is closed" to the loop.
            var link = new DroppableLink { ReviveOnAttempt = 1 };
            var oapa = Build(link);
            link.KillAfterCurrentCommand();

            await oapa.MoveRelative(Axis.XAxis, 1000, 10f, CancellationToken.None);

            link.ReopenCalls.Should().Be(1);
            link.X.Should().BeApproximately(100f, 0.01f, "the commanded 10' at ratio 10 completed after the recovery");
        }

        [Test]
        public async Task ALinkThatStaysDead_FailsAfterTheBoundedReopenSchedule() {
            // A controller that is really gone (power lost, cable out) must still fail -
            // but only after all three reopen attempts, so the log tells the story.
            var link = new DroppableLink { ReviveOnAttempt = int.MaxValue };
            var oapa = Build(link);
            link.KillAfterCurrentCommand();

            var act = () => oapa.MoveRelative(Axis.XAxis, 1000, 10f, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
            link.ReopenCalls.Should().Be(3, "the recovery gives up only after the full bounded schedule");
        }
    }
}
