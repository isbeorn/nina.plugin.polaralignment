using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The controller's event log copied into N.I.N.A.'s: every event once, in order, a board
    /// that restarted read from its first event, firmware without the log asked only once.
    /// </summary>
    public class OapaBoardEventsTest {

        /// <summary>A board whose log holds these texts, numbered from 1, answering $G= as the firmware does.</summary>
        internal static Func<uint, string> BoardWith(params string[] texts) => BoardOf(new List<string>(texts));

        private static Func<uint, string> BoardOf(List<string> texts) => after => {
            var last = (uint)texts.Count;
            return after < last
                ? FormattableString.Invariant($"<G|seq:{after + 1}|last:{last}|ms:{1500 * (after + 1)}|code:leg|text:{texts[(int)after]}|>")
                : FormattableString.Invariant($"<G|seq:-|last:{last}|>");
        };

        [Test]
        public void EveryEventAfterTheLastCopied_IsCopiedOnce_InOrder() {
            var log = new List<string> { "ALT (Y) +2.21' (+298 steps)", "ALT (Y) -2.14' (-289 steps)" };
            var board = BoardOf(log);
            var events = new OapaBoardEvents();

            events.Drain(board).Should().Equal("1.5 s leg: ALT (Y) +2.21' (+298 steps)", "3.0 s leg: ALT (Y) -2.14' (-289 steps)");
            events.Drain(board).Should().BeEmpty();

            log.Add("AZ (X) +0.28' (+4 steps)");
            events.Drain(board).Should().Equal("4.5 s leg: AZ (X) +0.28' (+4 steps)");
        }

        [Test]
        public void ABoardThatRestarted_IsReadFromItsFirstEvent() {
            var events = new OapaBoardEvents();
            events.Drain(BoardWith("a", "b", "c")).Should().HaveCount(3);

            // Its numbering starts over: "last" is below what was already copied.
            events.Drain(BoardWith("boot")).Should().Equal("1.5 s leg: boot");
        }

        [Test]
        public void FirmwareWithoutTheEventLog_IsAskedOnlyOnce() {
            var queries = 0;
            var events = new OapaBoardEvents();

            events.Drain(_ => { queries++; return "ok"; }).Should().BeEmpty();
            events.Drain(_ => { queries++; return "ok"; }).Should().BeEmpty();

            queries.Should().Be(1, "firmware 1.3.0 acknowledges an unknown command with ok");
        }
    }
}
