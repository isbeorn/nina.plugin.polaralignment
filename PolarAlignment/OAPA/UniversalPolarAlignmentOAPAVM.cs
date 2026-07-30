using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Plugins.PolarAlignment.OAPA;

namespace NINA.Plugins.PolarAlignment.OAPA {
    public partial class UniversalPolarAlignmentOAPAVM : UniversalPolarAlignmentBaseVM {
        public UniversalPolarAlignmentOAPAVM(IProfileService profileService) : base(profileService) { }

        protected override string SystemName => "OAPA System";

        protected override IPolarAlignmentSystem CreateSystem() => new UniversalPolarAlignmentOAPA();

        public override bool DoAutomatedAdjustments {
            get => Properties.Settings.Default.DoAutomatedAdjustments;
            set {
                Properties.Settings.Default.DoAutomatedAdjustments = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override double AutomatedAdjustmentSettleTime {
            get => Properties.Settings.Default.AutomatedAdjustmentSettleTime;
            set {
                Properties.Settings.Default.AutomatedAdjustmentSettleTime = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float XGearRatio {
            get => Properties.Settings.Default.OAPAXGearRatio;
            set {
                if (value < 1) { value = 1; }
                Properties.Settings.Default.OAPAXGearRatio = value;
                if (upa != null) { upa.XGearRatio = value; }
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(PositionX));
            }
        }

        public override int XSpeed {
            get => Properties.Settings.Default.OAPAXSpeed;
            set {
                Properties.Settings.Default.OAPAXSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float YGearRatio {
            get => Properties.Settings.Default.OAPAYGearRatio;
            set {
                if (value < 1) { value = 1; }
                Properties.Settings.Default.OAPAYGearRatio = value;
                if (upa != null) { upa.YGearRatio = value; }
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(PositionY));
            }
        }

        public override int YSpeed {
            get => Properties.Settings.Default.OAPAYSpeed;
            set {
                Properties.Settings.Default.OAPAYSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool ReverseAzimuth {
            get => Properties.Settings.Default.OAPAReverseAzimuth;
            set {
                Properties.Settings.Default.OAPAReverseAzimuth = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool ReverseAltitude {
            get => Properties.Settings.Default.OAPAReverseAltitude;
            set {
                Properties.Settings.Default.OAPAReverseAltitude = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float XBacklashCompensation {
            get => Properties.Settings.Default.OAPAXBacklashCompensation;
            set {
                Properties.Settings.Default.OAPAXBacklashCompensation = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double MaxCorrectionMagnitude {
            get => Properties.Settings.Default.OAPAMaxCorrectionMagnitude;
            set {
                Properties.Settings.Default.OAPAMaxCorrectionMagnitude = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        // OAPA hardware tolerates the faster profile: error-scaled probes and the 75%
        // correction candidate. UPAS and other systems keep the legacy behavior.
        public override bool AggressiveCorrectionProfile => true;

        /// <summary>
        /// OAPA correction-limit policy: scale with the measured error (80% of the current
        /// total error) so multi-degree initial errors converge in a handful of cycles,
        /// with the controller default as the floor for a gentle final approach and the
        /// user setting as a pure safety ceiling.
        /// </summary>
        public override double GetMaximumCorrectionMagnitude(double currentTotalErrorArcmin) {
            var autoScaled = System.Math.Max(AutomatedAdjustmentController.DefaultMaximumMoveMagnitude, currentTotalErrorArcmin * 0.8);
            return System.Math.Min(autoScaled, MaxCorrectionMagnitude);
        }

        public int XRunCurrent {
            get => Properties.Settings.Default.OAPAXRunCurrent;
            set {
                Properties.Settings.Default.OAPAXRunCurrent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetXRunCurrent(value);
                }
            }
        }

        public int YRunCurrent {
            get => Properties.Settings.Default.OAPAYRunCurrent;
            set {
                Properties.Settings.Default.OAPAYRunCurrent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetYRunCurrent(value);
                }
            }
        }

        public int XHoldPercent {
            get => Properties.Settings.Default.OAPAXHoldPercent;
            set {
                Properties.Settings.Default.OAPAXHoldPercent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetXHoldPercent(value);
                }
            }
        }

        public int YHoldPercent {
            get => Properties.Settings.Default.OAPAYHoldPercent;
            set {
                Properties.Settings.Default.OAPAYHoldPercent = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                if (upa?.Connected == true && upa is UniversalPolarAlignmentOAPA oapa) {
                    oapa.SetYHoldPercent(value);
                }
            }
        }
    }
}
