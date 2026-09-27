using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The panel's STOP command, from the button down to the wire. The driver-level half of
    /// this lives in the wire tests; what is pinned here is the part the user meets: when the
    /// button is offered at all, and that pressing it reaches the controller.
    /// </summary>
    public class OapaStopCommandTest {

        /// <summary>Scripted controller: answers everything, and can be told to fail the halt.</summary>
        private sealed class ScriptedLink : ISerialLink {
            public readonly List<string> Writes = new();
            private readonly Queue<string> pending = new();

            public bool FailTheHalt;

            public bool IsOpen => true;

            public void WriteLine(string text) {
                Writes.Add(text);
                if (text == "!" && FailTheHalt) { throw new System.IO.IOException("the port is gone"); }
                if (text == "?") {
                    pending.Enqueue("<Idle|MPos:0.00,0.00,0.00|V:1.2.2|>");
                    pending.Enqueue("ok");
                } else {
                    pending.Enqueue("ok");
                }
            }

            public string ReadLine() {
                if (pending.Count == 0) { throw new TimeoutException("no reply"); }
                return pending.Dequeue();
            }

            public void Dispose() { }
        }

        private sealed class TestableOapa : UniversalPolarAlignmentOAPA {
            public TestableOapa(ISerialLink link) : base(link) { }
        }

        /// <summary>A system that is not an OAPA controller, to prove the command is selective.</summary>
        private sealed class ForeignSystem : IPolarAlignmentSystem {
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
            public Task MoveRelative(Axis axis, int speed, float position, CancellationToken token) => Task.CompletedTask;
            public Task MoveAbsolute(Axis axis, int speed, float position, CancellationToken token) => Task.CompletedTask;
            public Task RefreshStatus(CancellationToken token) => Task.CompletedTask;
            public void Dispose() { }
        }

        private static OapaTestVm Vm(ScriptedLink link) {
            var vm = new OapaTestVm { Hardware = new TestableOapa(link) };
            link.Writes.Clear();
            return vm;
        }

        [Test]
        public void TheButtonIsOfferedWheneverTheControllerIsConnected() {
            // Deliberately not gated on IsNotMoving: a stop is worth having exactly while
            // something is moving, which is when every other control here is disabled.
            var vm = new OapaTestVm();

            vm.Connected = false;
            vm.CanStopMotion().Should().BeFalse("there is nothing to stop without a controller");

            vm.Connected = true;
            vm.IsNotMoving = false;
            vm.CanStopMotion().Should().BeTrue("a moving axis is precisely when Stop is needed");
        }

        [Test]
        public void PressingStop_PutsTheHaltOnTheWire() {
            var link = new ScriptedLink();
            var vm = Vm(link);

            vm.StopMotionCommand.Execute(null);

            link.Writes.Should().Contain("!", "the halt is a single character on the wire");
        }

        [Test]
        public void AHaltThatFails_DoesNotThrowAtTheUser() {
            // The failure path reports through a notification; what must never happen is the
            // command throwing out of the button handler, which would leave the panel dead
            // while the axis is still moving.
            var link = new ScriptedLink { FailTheHalt = true };
            var vm = Vm(link);

            var act = () => vm.StopMotionCommand.Execute(null);

            act.Should().NotThrow();
            link.Writes.Should().Contain("!", "it was attempted, it just did not land");
        }

        [Test]
        public void WithAControllerThatIsNotAnOapa_TheCommandDoesNothing() {
            // The panel is OAPA's, but the view model is reachable with whatever system the
            // tests or a future driver hand it: the halt is an OAPA command and must not be
            // sent to anything else.
            var vm = new OapaTestVm { Hardware = new ForeignSystem() };

            var act = () => vm.StopMotionCommand.Execute(null);

            act.Should().NotThrow();
        }
    }
}
