using FluentAssertions;
using NINA.Plugins.PolarAlignment.Instructions;

namespace NINA.Plugins.PolarAlignment.Test {

    public class AutoFinishGateTest {

        [Test]
        public void SingleBelowToleranceSolve_DoesNotFinish() {
            var gate = new AutoFinishGate(2);

            gate.Register(belowTolerance: true).Should().BeFalse();
            gate.Consecutive.Should().Be(1);
        }

        [Test]
        public void TwoConsecutiveBelowToleranceSolves_Finish() {
            var gate = new AutoFinishGate(2);

            gate.Register(belowTolerance: true).Should().BeFalse();
            gate.Register(belowTolerance: true).Should().BeTrue();
        }

        [Test]
        public void AboveToleranceSolve_ResetsPendingConfirmation() {
            var gate = new AutoFinishGate(2);

            gate.Register(belowTolerance: true).Should().BeFalse();
            gate.Register(belowTolerance: false).Should().BeFalse();
            gate.Consecutive.Should().Be(0);

            // A fresh single below-tolerance solve must not finish after the reset.
            gate.Register(belowTolerance: true).Should().BeFalse();
            gate.Register(belowTolerance: true).Should().BeTrue();
        }

        [Test]
        public void PendingConfirmation_IsReportedForHoldingCorrections() {
            var gate = new AutoFinishGate(2);

            gate.Consecutive.Should().Be(0, "corrections may run before any below-tolerance solve");
            gate.Register(belowTolerance: true);
            gate.Consecutive.Should().BePositive("corrections must hold while a confirmation solve is pending");
        }

        [Test]
        public void RequiredConsecutiveOfOne_FinishesImmediately() {
            var gate = new AutoFinishGate(1);

            gate.Register(belowTolerance: true).Should().BeTrue();
        }
    }
}
