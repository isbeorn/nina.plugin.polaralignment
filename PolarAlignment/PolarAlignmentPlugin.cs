using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using NINA.Core;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace NINA.Plugins.PolarAlignment {
    [Export(typeof(IPluginManifest))]
    class PolarAlignmentPlugin : PluginBase {
        [ImportingConstructor]
        public PolarAlignmentPlugin() {
            if (Properties.Settings.Default.UpdateSettings) {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public bool DefaultEastDirection {
            get {
                return Properties.Settings.Default.DefaultEastDirection;
            }
            set {
                Properties.Settings.Default.DefaultEastDirection = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public bool RefractionAdjustment {
            get {
                return Properties.Settings.Default.RefractionAdjustment;
            }
            set {
                Properties.Settings.Default.RefractionAdjustment = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public int Elevation {
            get {
                return Properties.Settings.Default.Elevation;
            }
            set {
                Properties.Settings.Default.Elevation = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public double DefaultMoveRate {
            get {
                return Properties.Settings.Default.DefaultMoveRate;
            }
            set {
                Properties.Settings.Default.DefaultMoveRate = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public int DefaultTargetDistance {
            get {
                return Properties.Settings.Default.DefaultTargetDistance;
            }
            set {
                Properties.Settings.Default.DefaultTargetDistance = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public double DefaultSearchRadius {
            get {
                return Properties.Settings.Default.DefaultSearchRadius;
            }
            set {
                Properties.Settings.Default.DefaultSearchRadius = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public double DefaultAltitudeOffset {
            get {
                return Properties.Settings.Default.DefaultAltitudeOffset;
            }
            set {
                Properties.Settings.Default.DefaultAltitudeOffset = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public double DefaultAzimuthOffset {
            get {
                return Properties.Settings.Default.DefaultAzimuthOffset;
            }
            set {
                Properties.Settings.Default.DefaultAzimuthOffset = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public double MoveTimeoutFactor {
            get {
                return Properties.Settings.Default.MoveTimeoutFactor;
            }
            set {
                Properties.Settings.Default.MoveTimeoutFactor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }
        
        public Color AltitudeErrorColor {
            get {
                return Properties.Settings.Default.AltitudeErrorColor;
            }
            set {
                Properties.Settings.Default.AltitudeErrorColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public Color AzimuthErrorColor {
            get {
                return Properties.Settings.Default.AzimuthErrorColor;
            }
            set {
                Properties.Settings.Default.AzimuthErrorColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public Color TotalErrorColor {
            get {
                return Properties.Settings.Default.TotalErrorColor;
            }
            set {
                Properties.Settings.Default.TotalErrorColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }
        
        public Color TargetCircleColor {
            get {
                return Properties.Settings.Default.TargetCircleColor;
            }
            set {
                Properties.Settings.Default.TargetCircleColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }
        public Color SuccessColor {
            get {
                return Properties.Settings.Default.SuccessColor;
            }
            set {
                Properties.Settings.Default.SuccessColor = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        public bool LogError {
            get {
                return Properties.Settings.Default.LogError;
            }
            set {
                Properties.Settings.Default.LogError = value;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }
        
    }
}
