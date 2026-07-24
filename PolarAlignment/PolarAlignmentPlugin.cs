using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using NINA.Core;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Plugins.PolarAlignment.Avalon;
using NINA.Plugins.PolarAlignment.OAPA;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.PlateSolving.Interfaces;

namespace NINA.Plugins.PolarAlignment {
    [Export(typeof(IPluginManifest))]
    public class PolarAlignmentPlugin : PluginBase, INotifyPropertyChanged {
        public static UniversalPolarAlignmentVM UniversalPolarAlignmentVM { get; private set; }
        public static UniversalPolarAlignmentOAPAVM UniversalPolarAlignmentOAPAVM { get; private set; }

        public static IPolarAlignmentSystemVM ActiveAlignmentSystemVM =>
            Properties.Settings.Default.SelectedPolarAlignmentSystem switch {
                "UPAS" => UniversalPolarAlignmentVM,
                "OAPA" => UniversalPolarAlignmentOAPAVM,
                _ => null
            };

        public PolarAlignmentSystemType SelectedPolarAlignmentSystem {
            get {
                return Enum.TryParse<PolarAlignmentSystemType>(Properties.Settings.Default.SelectedPolarAlignmentSystem, out var result)
                    ? result : PolarAlignmentSystemType.None;
            }
            set {
                Properties.Settings.Default.SelectedPolarAlignmentSystem = value.ToString();
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsSystemSelected));
                RaisePropertyChanged(nameof(IsUPASSelected));
                RaisePropertyChanged(nameof(IsOAPASelected));
                RaisePropertyChanged(nameof(ActiveSystem));
            }
        }

        public bool IsSystemSelected => SelectedPolarAlignmentSystem != PolarAlignmentSystemType.None;
        public bool IsUPASSelected => SelectedPolarAlignmentSystem == PolarAlignmentSystemType.UPAS;
        public bool IsOAPASelected => SelectedPolarAlignmentSystem == PolarAlignmentSystemType.OAPA;

        /// <summary>Instance wrapper for XAML binding with PropertyChanged support.</summary>
        public IPolarAlignmentSystemVM ActiveSystem => ActiveAlignmentSystemVM;

        public static string PluginId { get; private set; }

        [ImportingConstructor]
        public PolarAlignmentPlugin(
            IProfileService profileService,
            IImagingMediator imagingMediator,
            ITelescopeMediator telescopeMediator,
            IPlateSolverFactory plateSolverFactory) {
            if (Properties.Settings.Default.UpdateSettings) {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
            ResetSettingsCommand = new GalaSoft.MvvmLight.Command.RelayCommand(ResetSettings);
            UniversalPolarAlignmentVM = new UniversalPolarAlignmentVM(profileService);
            UniversalPolarAlignmentOAPAVM = new UniversalPolarAlignmentOAPAVM(
                profileService, imagingMediator, telescopeMediator, plateSolverFactory);
            PluginId = this.Identifier;
        }

        public ICommand ResetSettingsCommand { get; }

        private void ResetSettings() {
            try {
                if(MyMessageBox.Show($"This will reset all TPPA settings to their defaults. {Environment.NewLine}Are you sure?", "Reset All Settings", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes) {
                    Properties.Settings.Default.Reset();
                    CoreUtil.SaveSettings(Properties.Settings.Default);
                    RaisePropertyChanged(null);
                    UniversalPolarAlignmentVM.RaiseAllPropertiesChanged();
                    UniversalPolarAlignmentOAPAVM.RaiseAllPropertiesChanged();
                }
            } catch(Exception ex) {
                Logger.Error(ex);
            }
            
        }

        public bool DefaultEastDirection {
            get {
                return Properties.Settings.Default.DefaultEastDirection;
            }
            set {
                Properties.Settings.Default.DefaultEastDirection = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool RefractionAdjustment {
            get {
                return Properties.Settings.Default.RefractionAdjustment;
            }
            set {
                Properties.Settings.Default.RefractionAdjustment = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool UseContinuousErrorEstimator {
            get {
                return Properties.Settings.Default.UseContinuousErrorEstimator;
            }
            set {
                Properties.Settings.Default.UseContinuousErrorEstimator = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double DefaultMoveRate {
            get {
                return Properties.Settings.Default.DefaultMoveRate;
            }
            set {
                Properties.Settings.Default.DefaultMoveRate = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public int DefaultTargetDistance {
            get {
                return Properties.Settings.Default.DefaultTargetDistance;
            }
            set {
                Properties.Settings.Default.DefaultTargetDistance = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double DefaultSearchRadius {
            get {
                return Properties.Settings.Default.DefaultSearchRadius;
            }
            set {
                Properties.Settings.Default.DefaultSearchRadius = Math.Max(30, Math.Min(180, value)); ;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double DefaultAltitudeOffset {
            get {
                return Properties.Settings.Default.DefaultAltitudeOffset;
            }
            set {
                Properties.Settings.Default.DefaultAltitudeOffset = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double DefaultAzimuthOffset {
            get {
                return Properties.Settings.Default.DefaultAzimuthOffset;
            }
            set {
                Properties.Settings.Default.DefaultAzimuthOffset = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double MoveTimeoutFactor {
            get {
                return Properties.Settings.Default.MoveTimeoutFactor;
            }
            set {
                Properties.Settings.Default.MoveTimeoutFactor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }
        
        public Color AltitudeErrorColor {
            get {
                return Properties.Settings.Default.AltitudeErrorColor;
            }
            set {
                Properties.Settings.Default.AltitudeErrorColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public Color AzimuthErrorColor {
            get {
                return Properties.Settings.Default.AzimuthErrorColor;
            }
            set {
                Properties.Settings.Default.AzimuthErrorColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public Color TotalErrorColor {
            get {
                return Properties.Settings.Default.TotalErrorColor;
            }
            set {
                Properties.Settings.Default.TotalErrorColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }
        
        public Color TargetCircleColor {
            get {
                return Properties.Settings.Default.TargetCircleColor;
            }
            set {
                Properties.Settings.Default.TargetCircleColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }
        public Color SuccessColor {
            get {
                return Properties.Settings.Default.SuccessColor;
            }
            set {
                Properties.Settings.Default.SuccessColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double AlignmentTolerance {
            get {
                return Properties.Settings.Default.AlignmentTolerance;
            }
            set {
                if(value < 0) { value = 0; }
                Properties.Settings.Default.AlignmentTolerance = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public double MaxAutomatedCorrectionMagnitude {
            get {
                return Properties.Settings.Default.MaxAutomatedCorrectionMagnitude;
            }
            set {
                if (value < AutomatedAdjustmentController.MinimumConfigurableMoveMagnitude) { value = AutomatedAdjustmentController.MinimumConfigurableMoveMagnitude; }
                if (value > AutomatedAdjustmentController.MaximumConfigurableMoveMagnitude) { value = AutomatedAdjustmentController.MaximumConfigurableMoveMagnitude; }
                Properties.Settings.Default.MaxAutomatedCorrectionMagnitude = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool LogError {
            get {
                return Properties.Settings.Default.LogError;
            }
            set {
                Properties.Settings.Default.LogError = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool StopTrackingWhenDone {
            get {
                return Properties.Settings.Default.StopTrackingWhenDone;
            }
            set {
                Properties.Settings.Default.StopTrackingWhenDone = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public bool AutoPause {
            get {
                return Properties.Settings.Default.AutoPause;
            }
            set {
                Properties.Settings.Default.AutoPause = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

    }
}
