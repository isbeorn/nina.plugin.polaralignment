namespace NINA.Plugins.PolarAlignment.OAPA {

    /// <summary>
    /// One-time repairs to persisted OAPA parameters, run before anything reads them.
    ///
    /// Settings outlive the code that wrote them, so a release that changes what a stored
    /// value <em>means</em> has to say so explicitly: a rig that never re-calibrates would
    /// otherwise keep the old meaning forever. <see cref="Properties.Settings.OAPABacklashPairSchema"/>
    /// records which meaning the stored pair carries.
    /// </summary>
    public static class OapaSettingsMigration {

        /// <summary>Schema 1: the per-direction backlash pair is gated on the directionality verdict.</summary>
        private const int PerDirectionPairIsGated = 1;

        /// <summary>Schema 2: calibration displacements are projection-corrected (rc16).</summary>
        private const int ProjectionCorrectedCalibration = 2;

        /// <summary>Sentinel for "this direction was never set": the axis is treated as symmetric.</summary>
        private const float NotSet = -1f;

        private static readonly object gate = new();

        /// <summary>
        /// Whether schema 0 is carrying a direction split to undo, or is simply a profile that
        /// has never run this plugin.
        ///
        /// The two arrive here identically - schema 0 is also the factory value - and they must
        /// not be told the same story. Announcing a repair to somebody who has never calibrated
        /// says a previous release left them a bad value and asks them to re-run something they
        /// have never run. The schema moves either way; only the claim is conditional.
        /// </summary>
        internal static bool HadStoredPair(float storedX, float storedY)
            => storedX != NotSet || storedY != NotSet;

        /// <summary>
        /// Brings the persisted parameters up to the current schema. Idempotent and cheap;
        /// call it from anywhere that is about to read them.
        /// </summary>
        public static void EnsureCurrent() {
            lock (gate) {
                var settings = Properties.Settings.Default;
                if (settings.OAPABacklashPairSchema >= ProjectionCorrectedCalibration) { return; }

                if (settings.OAPABacklashPairSchema >= PerDirectionPairIsGated) {
                    // Schema 1 -> 2: the calibration now corrects the geometric projection of the
                    // pointing (altitude by the signed cos of the field azimuth, azimuth by no
                    // longer converting to an on-sky angle). Stored factors calibrated far from
                    // the meridian carry the old projection error; the values are left untouched
                    // - behaviour is exactly as before until the user re-calibrates - but one
                    // Self-Calibration and Apply per axis picks up the corrected geometry.
                    settings.OAPABacklashPairSchema = ProjectionCorrectedCalibration;
                    settings.Save();
                    NINA.Core.Utility.Logger.Info(
                        "OAPA: calibration geometry is projection-corrected in this release. Stored factors keep " +
                        "their previous behaviour; re-run the Self-Calibration and Apply once per axis to adopt " +
                        "the corrected measurement (required if you calibrate away from the meridian).");
                    return;
                }

                // Schema 0 wrote the two measured transitions into the pair unconditionally,
                // including when the calibration had already judged their difference
                // unestablished. A two-leg reversal travels `move - outward + back`, so that
                // difference became a floor no correction could get under - two field rigs
                // stalled at exactly theirs. The values are unrecoverable from here (only a
                // new calibration knows the verdict), so the negative direction goes back to
                // "not set", which makes both axes symmetric again and restores the behaviour
                // of the release before the pair existed. One Self-Calibration and Apply
                // re-establishes a genuine directional pair on the rigs that have one.
                // Schema 0 is also what a machine that has never run this plugin carries, and
                // the two cases must not be told the same story. On a fresh profile both
                // directions already hold the sentinel, so there is nothing to repair - and
                // announcing a repair to somebody who has never calibrated tells them a
                // previous release left them a bad value and that they should re-run something
                // they have never run. The schema still moves; only the claim is withheld.
                var hadStoredPair = HadStoredPair(settings.OAPAXBacklashCompensationNegative,
                                                  settings.OAPAYBacklashCompensationNegative);

                settings.OAPAXBacklashCompensationNegative = NotSet;
                settings.OAPAYBacklashCompensationNegative = NotSet;
                settings.OAPABacklashPairSchema = ProjectionCorrectedCalibration;
                settings.Save();

                if (hadStoredPair) {
                    NINA.Core.Utility.Logger.Info(
                        "OAPA: per-direction backlash reset to symmetric - the previous release stored a direction " +
                        "difference the calibration had not established, which biases every reversal. " +
                        "Re-run the Self-Calibration and Apply to measure it again.");
                }
            }
        }
    }
}
