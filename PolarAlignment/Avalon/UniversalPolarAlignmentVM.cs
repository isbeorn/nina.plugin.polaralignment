using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Plugins.PolarAlignment.Avalon;

namespace NINA.Plugins.PolarAlignment.Avalon {
    public partial class UniversalPolarAlignmentVM : UniversalPolarAlignmentBaseVM {
        public UniversalPolarAlignmentVM(IProfileService profileService) : base(profileService) { }

        protected override string SystemName => "Avalon Polar Alignment System";

        protected override IPolarAlignmentSystem CreateSystem() => new UniversalPolarAlignment();

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
            get => Properties.Settings.Default.AvalonXGearRatio;
            set {
                if (value < 1) { value = 1; }
                Properties.Settings.Default.AvalonXGearRatio = value;
                if (upa != null) { upa.XGearRatio = value; }
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(PositionX));
            }
        }

        public override int XSpeed {
            get => Properties.Settings.Default.AvalonXSpeed;
            set {
                Properties.Settings.Default.AvalonXSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float YGearRatio {
            get => Properties.Settings.Default.AvalonYGearRatio;
            set {
                if (value < 1) { value = 1; }
                Properties.Settings.Default.AvalonYGearRatio = value;
                if (upa != null) { upa.YGearRatio = value; }
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(PositionY));
            }
        }

        public override int YSpeed {
            get => Properties.Settings.Default.AvalonYSpeed;
            set {
                Properties.Settings.Default.AvalonYSpeed = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool ReverseAzimuth {
            get => Properties.Settings.Default.AvalonReverseAzimuth;
            set {
                Properties.Settings.Default.AvalonReverseAzimuth = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override bool ReverseAltitude {
            get => Properties.Settings.Default.AvalonReverseAltitude;
            set {
                Properties.Settings.Default.AvalonReverseAltitude = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float XBacklashCompensation {
            get => Properties.Settings.Default.AvalonXBacklashCompensation;
            set {
                Properties.Settings.Default.AvalonXBacklashCompensation = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public override float YBacklashCompensation {
            get => 0;
            set { }
        }
    }
}
