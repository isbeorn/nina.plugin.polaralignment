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
using NINA.Core.Utility.Notification;
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
                RaisePropertyChanged(nameof(IsLocalSystemSelected));
                RaisePropertyChanged(nameof(IsExternalSelected));
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
            IPlateSolverFactory plateSolverFactory,
            ICameraMediator cameraMediator,
            IMessageBroker messageBroker) {
            if (Properties.Settings.Default.UpdateSettings) {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
            ResetSettingsCommand = new GalaSoft.MvvmLight.Command.RelayCommand(ResetSettings);
            UniversalPolarAlignmentVM = new UniversalPolarAlignmentVM(profileService);
            UniversalPolarAlignmentOAPAVM = new UniversalPolarAlignmentOAPAVM(
                profileService, imagingMediator, telescopeMediator, plateSolverFactory, messageBroker);
            PluginId = this.Identifier;

            // The external correction endpoint has to exist before the first session starts, because a
            // controller announces itself as soon as NINA loads it.
            Bridge.BridgeHub.EnsureInitialized(messageBroker);

            // The list of selectable systems follows the broker: a controller that switches its assignment on
            // appears in it at once, and one that stops answering leaves it again. Every refresh is marshalled to
            // the UI thread, because this constructor is not guaranteed to run on it. The timer covers the
            // controllers that leave (only the presence window can tell), the event covers the ones that arrive.
            Bridge.BridgeHub.Instance.RegisteredControllersChanged += (_, __) => RefreshSystemOptionsOnUiThread();
            systemListTimer = new System.Timers.Timer(1000) { AutoReset = true };
            systemListTimer.Elapsed += (_, __) => RefreshSystemOptionsOnUiThread();
            RefreshSystemOptionsOnUiThread();
            systemListTimer.Start();
        }

        public ICommand ResetSettingsCommand { get; }

        private readonly System.Timers.Timer systemListTimer;

        /// <summary>
        /// Every alignment system the operator can select: the built-in ones plus one entry per controller that is
        /// registered on the message broker. External correction only runs for the entry selected here.
        /// </summary>
        public System.Collections.ObjectModel.ObservableCollection<SystemOption> SystemOptions { get; } =
            new System.Collections.ObjectModel.ObservableCollection<SystemOption>();

        private SystemOption selectedSystemOption;
        private bool systemListInitialized;
        private readonly Dictionary<Guid, string> knownControllers = new Dictionary<Guid, string>();

        /// <summary>
        /// The selected entry of <see cref="SystemOptions"/>. The settings keep the system type plus the broker
        /// identity of the controller, so the choice survives a restart and is matched again once that controller
        /// announces itself.
        /// </summary>
        public SystemOption SelectedSystemOption {
            get => selectedSystemOption;
            set {
                // WPF clears the selection while the entries are replaced; that is not an operator choice.
                if (value == null) { return; }

                selectedSystemOption = value;
                SelectedPolarAlignmentSystem = value.Type;
                SavedControllerSetting = value.ControllerId.HasValue ? value.ControllerId.Value.ToString("N") : string.Empty;
                CoreUtil.SaveSettings(Properties.Settings.Default);
                RaisePropertyChanged(nameof(SelectedControllerDisplay));
            }
        }

        /// <summary>The saved system type, read through the same guard as <see cref="SavedControllerSetting"/>.</summary>
        private static string SavedSystemSetting {
            get {
                try { return Properties.Settings.Default.SelectedPolarAlignmentSystem ?? string.Empty; } catch (Exception ex) {
                    Logger.Debug($"[Bridge] The saved system setting could not be read: {ex.Message}");
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// The saved system settings, read through a guard: a settings file that does not know the newer key yet
        /// must not keep the options page from listing the systems.
        /// </summary>
        private static string SavedControllerSetting {
            get {
                try { return Properties.Settings.Default.SelectedBridgeControllerId ?? string.Empty; } catch (Exception ex) {
                    Logger.Debug($"[Bridge] The saved controller setting could not be read: {ex.Message}");
                    return string.Empty;
                }
            }
            set {
                try { Properties.Settings.Default.SelectedBridgeControllerId = value; } catch (Exception ex) {
                    Logger.Debug($"[Bridge] The saved controller setting could not be written: {ex.Message}");
                }
            }
        }

        /// <summary>Label of the selected external controller, empty while a built-in system is selected.</summary>
        public string SelectedControllerDisplay =>
            selectedSystemOption?.ControllerId.HasValue == true ? selectedSystemOption.DisplayName : string.Empty;

        /// <summary>True while an external controller is the selected alignment system.</summary>
        public bool IsExternalSelected => SelectedPolarAlignmentSystem == PolarAlignmentSystemType.External;

        /// <summary>True while a built-in hardware system (UPAS or OAPA) is the selected alignment system.</summary>
        public bool IsLocalSystemSelected => SelectedPolarAlignmentSystem == PolarAlignmentSystemType.UPAS
                                             || SelectedPolarAlignmentSystem == PolarAlignmentSystemType.OAPA;

        /// <summary>
        /// Refreshes the system list on the UI thread: the list is bound to a combo box, while the timer and the
        /// plugin construction do not run on that thread.
        /// </summary>
        private void RefreshSystemOptionsOnUiThread() {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) { return; }

            if (dispatcher.CheckAccess()) {
                RefreshSystemOptions();
            } else {
                dispatcher.BeginInvoke(new Action(RefreshSystemOptions));
            }
        }

        /// <summary>
        /// Rebuilds <see cref="SystemOptions"/> from the settings and the controllers registered on the broker. The
        /// entries are only replaced when the list really changed, so the operator's selection does not flicker.
        /// </summary>
        private void RefreshSystemOptions() {
            var controllers = Bridge.BridgeHub.Instance?.RegisteredControllers
                              ?? (IReadOnlyList<Bridge.BridgeHub.RegisteredController>)new List<Bridge.BridgeHub.RegisteredController>();

            // The operator is told about a controller that appears or leaves before the list itself changes, so the
            // notification explains what the list is about to show instead of arriving after the fact.
            NotifyControllerChanges(controllers);

            var desired = new List<SystemOption> {
                new SystemOption(PolarAlignmentSystemType.None, "None", null),
                new SystemOption(PolarAlignmentSystemType.UPAS, "UPAS", null),
                new SystemOption(PolarAlignmentSystemType.OAPA, "OAPA", null)
            };
            foreach (var controller in controllers) {
                desired.Add(new SystemOption(PolarAlignmentSystemType.External,
                                             string.IsNullOrWhiteSpace(controller.Name) ? controller.Display : controller.Name,
                                             controller.SenderId));
            }

            var savedType = Enum.TryParse<PolarAlignmentSystemType>(SavedSystemSetting, out var parsedType)
                ? parsedType
                : PolarAlignmentSystemType.None;
            var savedControllerId = Guid.TryParse(SavedControllerSetting, out var parsedId)
                ? parsedId
                : (Guid?)null;
            var hadSelectedItem = SystemOptions.Any(option => IsSavedSelection(option, savedType, savedControllerId));

            if (!SameOptions(SystemOptions, desired)) {
                SystemOptions.Clear();
                foreach (var option in desired) { SystemOptions.Add(option); }
            }

            var selected = SystemOptions.FirstOrDefault(option => IsSavedSelection(option, savedType, savedControllerId));
            if (selected != null) {
                selectedSystemOption = selected;
                RaisePropertyChanged(nameof(SelectedSystemOption));
            } else if (hadSelectedItem) {
                // The selected controller left the broker: the selection is cleared with it, so the options page
                // never shows a system the controller side has switched off. A choice made before NINA started is
                // kept until the controller has been seen once.
                SelectedSystemOption = SystemOptions[0];
            } else {
                selectedSystemOption = null;
                RaisePropertyChanged(nameof(SelectedSystemOption));
            }
        }

        /// <summary>
        /// Tells the operator once when a controller appears on the broker or leaves it, so the system list is
        /// worth a look instead of being found by accident.
        /// </summary>
        private void NotifyControllerChanges(IReadOnlyList<Bridge.BridgeHub.RegisteredController> controllers) {
            var current = controllers.ToDictionary(controller => controller.SenderId, controller => controller.Display);

            if (systemListInitialized) {
                foreach (var controller in current.Where(entry => !knownControllers.ContainsKey(entry.Key))) {
                    Logger.Info($"[Bridge] {controller.Value} is available as an alignment system.");
                    Notification.ShowInformation($"{controller.Value} is available as an alignment system. Select it under \"Polar Alignment System\" to let it drive the alignment.", TimeSpan.FromMinutes(1));
                }
                foreach (var known in knownControllers.Where(entry => !current.ContainsKey(entry.Key))) {
                    Logger.Info($"[Bridge] {known.Value} left the alignment system list.");
                    var selectionNote = string.Equals(SavedControllerSetting, known.Key.ToString("N"), StringComparison.Ordinal)
                        ? " The alignment system selection was cleared with it."
                        : string.Empty;
                    Notification.ShowInformation($"{known.Value} is no longer connected to the message broker and is removed from the alignment system list.{selectionNote}", TimeSpan.FromMinutes(1));
                }
            }

            knownControllers.Clear();
            foreach (var controller in current) { knownControllers[controller.Key] = controller.Value; }
            systemListInitialized = true;
        }

        private static bool IsSavedSelection(SystemOption option, PolarAlignmentSystemType type, Guid? controllerId) =>
            option.Type == type && option.ControllerId == controllerId;

        private static bool SameOptions(IList<SystemOption> current, IList<SystemOption> desired) {
            if (current.Count != desired.Count) { return false; }

            for (var index = 0; index < current.Count; index++) {
                if (current[index].Type != desired[index].Type
                    || current[index].ControllerId != desired[index].ControllerId
                    || current[index].DisplayName != desired[index].DisplayName) {
                    return false;
                }
            }

            return true;
        }

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
