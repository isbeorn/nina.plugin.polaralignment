using FluentAssertions;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Plugins.PolarAlignment.Test {
    [TestFixture]
    public class TPAPAReferenceStarTrackingTest {
        private static readonly DateTime ObservationTime = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

        [Test]
        public async Task CorrectionFrames_WithoutManualLock_UseCenterWithoutDetection() {
            var fixture = new TrackingFixture();

            await fixture.Update(CreateCoordinates(10, 20));
            fixture.Clock.Advance(TimeSpan.FromSeconds(121));
            await fixture.Update(CreateCoordinates(40, -10));

            fixture.DetectionCallCount.Should().Be(0);
            fixture.ViewModel.ReferenceStar.Should().Be(fixture.ViewModel.Center);
            fixture.ViewModel.ReferenceStarCoordinates.Should().BeNull();
        }

        [Test]
        public async Task CorrectionFrames_WithManualLock_ProjectCoordinateWithoutRedetection() {
            var fixture = new TrackingFixture();
            var lockField = CreateCoordinates(10, 20);
            await fixture.ManualLock(lockField, new Point(2200, 1300));
            var manuallySelectedCoordinates = fixture.ViewModel.ReferenceStarCoordinates;
            var currentField = CreateCoordinates(10, 20.2);

            await fixture.Update(currentField);
            await fixture.Update(currentField);

            fixture.DetectionCallCount.Should().Be(1);
            var expected = manuallySelectedCoordinates.XYProjection(currentField,
                                                                     fixture.ViewModel.Center,
                                                                     fixture.ViewModel.ArcsecPerPix,
                                                                     fixture.ViewModel.ArcsecPerPix,
                                                                     0);
            fixture.ViewModel.ReferenceStar.X.Should().BeApproximately(expected.X, 1e-6);
            fixture.ViewModel.ReferenceStar.Y.Should().BeApproximately(expected.Y, 1e-6);
            fixture.ViewModel.ReferenceStarCoordinates.Should().BeSameAs(manuallySelectedCoordinates);
        }

        [Test]
        public async Task CorrectionFrame_ManualLock_RedetectsAfter120Seconds() {
            var fixture = new TrackingFixture();
            var field = CreateCoordinates(10, 20);
            await fixture.ManualLock(field, new Point(2200, 1300));
            var lockedCoordinates = fixture.ViewModel.ReferenceStarCoordinates;

            fixture.Clock.Advance(TimeSpan.FromSeconds(119));
            await fixture.Update(field);
            fixture.DetectionCallCount.Should().Be(1);

            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Update(field);
            fixture.DetectionCallCount.Should().Be(2);
            var expectedReference = lockedCoordinates.XYProjection(field,
                                                                    fixture.ViewModel.Center,
                                                                    fixture.ViewModel.ArcsecPerPix,
                                                                    fixture.ViewModel.ArcsecPerPix,
                                                                    0);
            fixture.LastDetectionReference.X.Should().BeApproximately(expectedReference.X, 1e-6);
            fixture.LastDetectionReference.Y.Should().BeApproximately(expectedReference.Y, 1e-6);
        }

        [Test]
        public async Task CorrectionFrame_ManualLock_RedetectsAfterFieldShiftOverHalfDegree() {
            var fixture = new TrackingFixture();
            var lockField = CreateCoordinates(10, 20);
            await fixture.ManualLock(lockField, new Point(2200, 1300));

            await fixture.Update(CreateCoordinates(10, 20.5));
            fixture.DetectionCallCount.Should().Be(1);

            await fixture.Update(CreateCoordinates(10, 20.51));
            fixture.DetectionCallCount.Should().Be(2);
        }

        [Test]
        public async Task CorrectionFrame_ManualLockOutsideImage_RedetectsAtCenterAndKeepsLock() {
            var fixture = new TrackingFixture();
            fixture.SetImageSize(400, 300);
            fixture.ViewModel.ArcsecPerPix = 1;
            var lockField = CreateCoordinates(10, 20);
            await fixture.ManualLock(lockField, fixture.ViewModel.Center);
            var originalCoordinates = fixture.ViewModel.ReferenceStarCoordinates;
            var shiftedField = CreateCoordinates(10, 20.1);

            await fixture.Update(shiftedField);

            fixture.DetectionCallCount.Should().Be(2);
            fixture.LastDetectionReference.Should().Be(fixture.ViewModel.Center);
            fixture.ViewModel.ReferenceStarCoordinates.Should().NotBeNull();
            fixture.ViewModel.ReferenceStarCoordinates.Should().NotBeSameAs(originalCoordinates);

            await fixture.Update(shiftedField);
            fixture.DetectionCallCount.Should().Be(2);
        }

        [Test]
        public async Task ManualRelock_ResetsRedetectionTimeBaseline() {
            var fixture = new TrackingFixture();
            var field = CreateCoordinates(10, 20);
            await fixture.ManualLock(field, new Point(2200, 1300));

            fixture.Clock.Advance(TimeSpan.FromSeconds(119));
            await fixture.ManualLock(field, new Point(2300, 1400));
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Update(field);

            fixture.DetectionCallCount.Should().Be(2);
        }

        private static Coordinates CreateCoordinates(double rightAscensionDegrees, double declinationDegrees) {
            return new Coordinates(Angle.ByDegree(rightAscensionDegrees),
                                   Angle.ByDegree(declinationDegrees),
                                   Epoch.J2000,
                                   new FixedTime(ObservationTime));
        }

        private sealed class TrackingFixture {
            public TrackingFixture() {
                Clock = new MutableTimeProvider(new DateTimeOffset(ObservationTime));
                ViewModel = new TPAPAVM(null, null, LocateReferenceStar, Clock) {
                    Center = new Point(2000, 1500),
                    ArcsecPerPix = 2
                };
            }

            public TPAPAVM ViewModel { get; }
            public MutableTimeProvider Clock { get; }
            public int DetectionCallCount { get; private set; }
            public Point LastDetectionReference { get; private set; }

            public void SetImageSize(int width, int height) {
                ViewModel.Image = new RenderedImageStub(width, height);
                ViewModel.Center = new Point(width / 2, height / 2);
            }

            public Task Update(Coordinates fieldCenter) {
                return ViewModel.UpdateReferenceStar(new PlateSolveResult() { Coordinates = fieldCenter },
                                                     null,
                                                     CancellationToken.None);
            }

            public Task ManualLock(Coordinates fieldCenter, Point selection) {
                return ViewModel.DetectAndLockReferenceStar(new PlateSolveResult() { Coordinates = fieldCenter },
                                                            selection,
                                                            null,
                                                            CancellationToken.None);
            }

            private Task<Point> LocateReferenceStar(IRenderedImage image,
                                                    Point projectedPoint,
                                                    IProgress<ApplicationStatus> progress,
                                                    CancellationToken token) {
                DetectionCallCount++;
                LastDetectionReference = projectedPoint;
                return Task.FromResult(new Point(projectedPoint.X + 3, projectedPoint.Y - 2));
            }
        }

        private sealed class MutableTimeProvider : TimeProvider {
            private DateTimeOffset utcNow;

            public MutableTimeProvider(DateTimeOffset utcNow) {
                this.utcNow = utcNow;
            }

            public override DateTimeOffset GetUtcNow() => utcNow;

            public void Advance(TimeSpan duration) {
                utcNow += duration;
            }
        }

        private sealed class RenderedImageStub : IRenderedImage {
            public RenderedImageStub(int width, int height) {
                Image = BitmapSource.Create(width,
                                            height,
                                            96,
                                            96,
                                            PixelFormats.Gray8,
                                            null,
                                            new byte[width * height],
                                            width);
                Image.Freeze();
            }

            public IImageData RawImageData => throw new NotSupportedException();
            public BitmapSource OriginalImage => Image;
            public BitmapSource Image { get; }

            public IDebayeredImage Debayer(bool saveColorChannels = false, bool saveLumChannel = false, SensorType bayerPattern = SensorType.RGGB) => throw new NotSupportedException();
            public IRenderedImage ReRender() => throw new NotSupportedException();
            public Task<IRenderedImage> Stretch(double factor, double blackClipping, bool unlinked) => throw new NotSupportedException();
            public Task<IRenderedImage> DetectStars(bool annotateImage,
                                                    StarSensitivityEnum sensitivity,
                                                    NoiseReductionEnum noiseReduction,
                                                    CancellationToken cancelToken = default,
                                                    IProgress<ApplicationStatus> progress = default!) => throw new NotSupportedException();
            public Task<BitmapSource> GetThumbnail() => throw new NotSupportedException();
            public void UpdateAnalysis(StarDetectionParams p, StarDetectionResult result) => throw new NotSupportedException();
        }

        private sealed class FixedTime : ICustomDateTime {
            private readonly DateTime time;

            public FixedTime(DateTime time) {
                this.time = time;
            }

            public DateTime Now => time;
            public DateTime UtcNow => time;
        }
    }
}
