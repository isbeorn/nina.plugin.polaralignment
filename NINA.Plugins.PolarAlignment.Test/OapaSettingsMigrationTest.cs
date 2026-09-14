using FluentAssertions;
using NINA.Plugins.PolarAlignment.OAPA;

namespace NINA.Plugins.PolarAlignment.Test {

    /// <summary>
    /// The one-time repairs to persisted OAPA parameters. They run every time the panel's VM
    /// is created, before anything reads a stored value, so each schema step has to change
    /// exactly what it claims and leave everything else alone.
    /// </summary>
    public class OapaSettingsMigrationTest {

        private const float NotSet = -1f;

        [Test]
        public void StoredPairsFromThePreviousRelease_AreResetToSymmetric() {
            // Settings outlive the code that wrote them: a rig that upgrades and does not
            // re-calibrate would otherwise keep driving with the gap that stalled it, because
            // the fix lives in the calibration and the calibration is not run on upgrade.
            // The verdict that produced the stored pair is gone, so the only honest move is
            // back to symmetric - which is exactly the behaviour of the release before the
            // pair existed.
            var settings = Properties.Settings.Default;
            settings.OAPABacklashPairSchema = 0;
            settings.OAPAXBacklashCompensation = 54.34f;
            settings.OAPAXBacklashCompensationNegative = 45.02f;
            settings.OAPAYBacklashCompensationNegative = 20.0f;

            OapaSettingsMigration.EnsureCurrent();

            settings.OAPAXBacklashCompensationNegative.Should().Be(NotSet, "-1 means 'not set', i.e. same as the positive direction");
            settings.OAPAYBacklashCompensationNegative.Should().Be(NotSet);
            settings.OAPAXBacklashCompensation.Should().Be(54.34f, "the measured magnitude is not in doubt, only the difference");
            settings.OAPABacklashPairSchema.Should().Be(2, "the schema-0 reset lands directly on the current schema");

            // Idempotent: a later genuine directional pair must survive re-entry.
            settings.OAPAXBacklashCompensationNegative = 4.01f;
            OapaSettingsMigration.EnsureCurrent();
            settings.OAPAXBacklashCompensationNegative.Should().Be(4.01f);
        }

        [Test]
        public void TheProjectionStep_OnlyMovesTheSchema_AndKeepsEveryStoredValue() {
            // Schema 1 already carries a pair the directionality verdict produced, so there is
            // nothing to undo: the release only changed how a *new* calibration measures. The
            // stored values keep their behaviour until the user re-calibrates, and resetting
            // them here would throw away a genuine directional pair on upgrade.
            var settings = Properties.Settings.Default;
            settings.OAPABacklashPairSchema = 1;
            settings.OAPAXGearRatio = 412f;
            settings.OAPAXBacklashCompensation = 4.01f;
            settings.OAPAXBacklashCompensationNegative = 2.63f;
            settings.OAPAYBacklashCompensationNegative = 1.5f;

            OapaSettingsMigration.EnsureCurrent();

            settings.OAPABacklashPairSchema.Should().Be(2);
            settings.OAPAXBacklashCompensationNegative.Should().Be(2.63f, "a schema-1 pair was gated by the verdict and stays");
            settings.OAPAYBacklashCompensationNegative.Should().Be(1.5f);
            settings.OAPAXBacklashCompensation.Should().Be(4.01f);
            settings.OAPAXGearRatio.Should().Be(412f);
        }
    }
}
