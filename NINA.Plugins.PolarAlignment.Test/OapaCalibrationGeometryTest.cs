using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;
using System;

namespace NINA.Plugins.PolarAlignment.Test {

    public class OapaCalibrationGeometryTest {

        private static CalibrationSolveSample Sample(double raDeg, double decDeg, double altDeg = 30, double azDeg = 0) =>
            new(raDeg, decDeg, altDeg, azDeg);

        [Test]
        public void SignedDisplacement_AltitudeAxis_TowardNorth_TransfersOneToOne() {
            var a = Sample(10, 0, altDeg: 60);
            var b = Sample(10, 0.5, altDeg: 60.5);
            OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: false, a, b).Should().BeApproximately(30.0, 0.01);
        }

        [Test]
        public void SignedDisplacement_AltitudeAxis_DividesByTheFieldAzimuthProjection() {
            // The altitude actuator tilts about the horizontal east-west axis: a field at
            // azimuth 137.2° shows only cos(137.2°) = -0.734 of the tilt in its altitude.
            // 30' of measured (negative) altitude displacement therefore means ~40.9' of
            // positive axis motion. This is the exact geometry of a field session whose
            // altitude factor came out 202.7 for a mechanism that delivered 85.
            var a = Sample(10, 0, altDeg: 58.0, azDeg: 137.2);
            var b = Sample(10, -0.5, altDeg: 57.5, azDeg: 137.2);
            OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: false, a, b)
                .Should().BeApproximately(30.0 / Math.Cos(137.2 * Math.PI / 180.0) * -1.0, 0.1);
        }

        [Test]
        public void SignedDisplacement_AltitudeAxis_SouthOfEastWest_ReversesTheSign() {
            // Toward the south meridian the projection is -1: the same axis motion moves the
            // field's altitude the opposite way. The signed division keeps the *axis* sign
            // stable across the sky, so the Reverse flag stays a property of the wiring.
            var north = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: false,
                Sample(10, 0, altDeg: 46, azDeg: 0), Sample(10, 0.5, altDeg: 46.5, azDeg: 0));
            var south = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: false,
                Sample(10, 0, altDeg: 46, azDeg: 180), Sample(10, 0.5, altDeg: 45.5, azDeg: 180));
            north.Should().BeApproximately(30.0, 0.01);
            south.Should().BeApproximately(30.0, 0.01,
                "a field dropping south of the mount is the same axis motion as a field rising north of it");
        }

        [Test]
        public void SignedDisplacement_AltitudeAxis_ProjectionIsFlooredForRestorePaths() {
            // Restore paths may run from a pointing the calibration itself would refuse; the
            // division must stay bounded rather than explode toward due east/west.
            var a = Sample(10, 0, altDeg: 45, azDeg: 90.0);
            var b = Sample(10, 0, altDeg: 45.5, azDeg: 90.0);
            var d = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: false, a, b);
            Math.Abs(d).Should().BeLessThanOrEqualTo(30.0 / OapaCalibrationGeometry.MinimumAltitudeCosAzimuth + 0.1);
        }

        [Test]
        public void SignedDisplacement_AzimuthAxis_TransfersTheCoordinateDelta_AtAnyAltitude() {
            // A base rotation about the vertical shifts every field's azimuth *coordinate* by
            // the rotation itself, regardless of altitude. Dividing by cos(alt) instead would
            // convert to an on-sky angle and silently scale every azimuth factor by
            // cos(field alt) — a 0.53 gain on a rig calibrating at alt 58°.
            var low = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: true,
                Sample(10, 0, altDeg: 10, azDeg: 100.0), Sample(10, 0.5, altDeg: 10, azDeg: 100.5));
            var high = OapaCalibrationGeometry.SignedAxisDisplacementArcmin(isAzimuthAxis: true,
                Sample(10, 0, altDeg: 60, azDeg: 100.0), Sample(10, 0.5, altDeg: 60, azDeg: 100.5));
            low.Should().BeApproximately(30.0, 0.05);
            high.Should().BeApproximately(30.0, 0.05, "the coordinate delta is the axis motion at any altitude");
        }

        [Test]
        public void SignedDisplacement_AltitudeRisesOnPositiveCommand_IsConsistent() {
            var a = Sample(10, 0, altDeg: 30);
            var b = Sample(10, 0.5, altDeg: 30.75);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, 45f).Should().BeTrue();
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, -45f).Should().BeFalse();
        }

        [Test]
        public void SignedDisplacement_AltitudeFallsOnPositiveCommand_IsInconsistent() {
            // A physically reversed altitude axis: the field drops when commanded up.
            var a = Sample(10, 0, altDeg: 30);
            var b = Sample(10, -0.5, altDeg: 29.25);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, 45f).Should().BeFalse();
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, -45f).Should().BeTrue();
        }

        [Test]
        public void SignedDisplacement_AHealthyAxisPointedSouthOfEast_IsNotMistakenForAReversedOne() {
            // The trap the signed projection exists for: at azimuth 137° a healthy axis
            // lowers the field's altitude on a positive command. Reading that raw would
            // flip the Reverse flag on every calibration done on that side of the sky.
            var a = Sample(10, 0, altDeg: 58, azDeg: 137.2);
            var b = Sample(10, -0.5, altDeg: 57.75, azDeg: 137.2);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: false, a, b, 45f).Should().BeTrue();
        }

        [Test]
        public void SignedDisplacement_AzimuthUsesTopocentricAzimuth() {
            var a = Sample(10, 0, azDeg: 100.0);
            var b = Sample(10.5, 0, azDeg: 100.75);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, a, b, 45f).Should().BeTrue();
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, a, b, -45f).Should().BeFalse();
        }

        [Test]
        public void SignedDisplacement_AzimuthAcrossNorth_KeepsItsSign() {
            // 359.9° -> 0.65° is +0.75° of azimuth, not -359.25°: the wrap must not flip the verdict.
            var a = Sample(10, 0, azDeg: 359.9);
            var b = Sample(10.5, 0, azDeg: 0.65);
            OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, a, b, 45f).Should().BeTrue();

            var back = OapaCalibrationGeometry.SignedDisplacementMatchesCommand(isAzimuthAxis: true, b, a, 45f);
            back.Should().BeFalse("0.65° -> 359.9° is negative azimuth motion");
        }
    }
}
