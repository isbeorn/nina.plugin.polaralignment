using FluentAssertions;

namespace NINA.Plugins.PolarAlignment.Test {
    public class BacklashCompensationPlannerTest {
        [Test]
        public void NegativeMovement_FromPositivePreload_DeliversRequestedPhysicalMovement() {
            const float backlash = 3f;
            const float requestedMovement = -10f;
            var axis = new LostMotionAxis(backlash, LastDirection.Positive);

            axis.Move(requestedMovement);
            var compensation = BacklashCompensationPlanner.CreateSequence(backlash, LastDirection.Negative);
            axis.Move(compensation.FirstMove);
            axis.Move(compensation.SecondMove);

            axis.PhysicalPosition.Should().Be(requestedMovement);
            axis.LoadedDirection.Should().Be(LastDirection.Positive);
        }

        [Test]
        public void PositiveMovement_FromPositivePreload_DoesNotAddCompensationMoves() {
            var compensation = BacklashCompensationPlanner.CreateSequence(3f, LastDirection.Positive);

            compensation.FirstMove.Should().Be(0);
            compensation.SecondMove.Should().Be(0);
        }

        [Test]
        public void NegativeConfiguredCompensation_IsTreatedAsMagnitude() {
            var compensation = BacklashCompensationPlanner.CreateSequence(-3f, LastDirection.Negative);

            compensation.FirstMove.Should().Be(-3f);
            compensation.SecondMove.Should().Be(3f);
        }

        [Test]
        public void AlternatingMovements_CompensateOnlyNegativeDirection() {
            const float backlash = 3f;
            var axis = new LostMotionAxis(backlash, LastDirection.Positive);
            var compensationMoveCount = 0;

            foreach (var movement in new[] { -10f, 10f, -10f, 10f }) {
                compensationMoveCount += MoveWithCompensation(axis, movement, backlash);
            }

            axis.PhysicalPosition.Should().Be(0);
            axis.LoadedDirection.Should().Be(LastDirection.Positive);
            compensationMoveCount.Should().Be(4);
        }

        [Test]
        public void RepeatedNegativeMovements_EachRestorePositivePreload() {
            const float backlash = 3f;
            var axis = new LostMotionAxis(backlash, LastDirection.Positive);

            var firstCompensationMoveCount = MoveWithCompensation(axis, -10f, backlash);
            var secondCompensationMoveCount = MoveWithCompensation(axis, -10f, backlash);

            axis.PhysicalPosition.Should().Be(-20f);
            axis.LoadedDirection.Should().Be(LastDirection.Positive);
            firstCompensationMoveCount.Should().Be(2);
            secondCompensationMoveCount.Should().Be(2);
        }

        [Test]
        public void ZeroCompensation_DoesNotAddMoves() {
            var compensation = BacklashCompensationPlanner.CreateSequence(0, LastDirection.Negative);

            compensation.FirstMove.Should().Be(0);
            compensation.SecondMove.Should().Be(0);
        }

        private static int MoveWithCompensation(LostMotionAxis axis, float movement, float backlash) {
            axis.Move(movement);
            var direction = movement >= 0 ? LastDirection.Positive : LastDirection.Negative;
            var compensation = BacklashCompensationPlanner.CreateSequence(backlash, direction);
            if (compensation.FirstMove == 0) { return 0; }

            axis.Move(compensation.FirstMove);
            axis.Move(compensation.SecondMove);
            return 2;
        }

        private sealed class LostMotionAxis {
            private readonly float backlash;

            public LostMotionAxis(float backlash, LastDirection loadedDirection) {
                this.backlash = backlash;
                LoadedDirection = loadedDirection;
            }

            public float PhysicalPosition { get; private set; }
            public LastDirection LoadedDirection { get; private set; }

            public void Move(float distance) {
                var direction = distance >= 0 ? LastDirection.Positive : LastDirection.Negative;
                if (direction == LoadedDirection) {
                    PhysicalPosition += distance;
                    return;
                }

                var effectiveDistance = Math.Max(Math.Abs(distance) - backlash, 0);
                PhysicalPosition += direction == LastDirection.Positive ? effectiveDistance : -effectiveDistance;
                if (Math.Abs(distance) >= backlash) {
                    LoadedDirection = direction;
                }
            }
        }
    }
}
