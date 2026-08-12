using FluentAssertions;
using NINA.Plugins.PolarAlignment.Avalon;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PluginSettings = NINA.Plugins.PolarAlignment.Properties.Settings;

namespace NINA.Plugins.PolarAlignment.Test {

    [Apartment(ApartmentState.STA)]
    [NonParallelizable]
    public class UPASDockableVisibilityTest {

        [Test]
        public void AutomationMode_ShowsOnlyAutomaticStatus() {
            var originalAutomationSetting = PluginSettings.Default.DoAutomatedAdjustments;
            var originalUPAS = PolarAlignmentPlugin.UniversalPolarAlignmentVM;

            try {
                PluginSettings.Default.DoAutomatedAdjustments = false;
                var upas = new UniversalPolarAlignmentVM(null!) {
                    Connected = true
                };
                SetUPAS(upas);
                var context = (TPAPAVM)RuntimeHelpers.GetUninitializedObject(typeof(TPAPAVM));
                var resources = new Options();
                var view = new ContentControl {
                    Content = context,
                    ContentTemplate = (DataTemplate)resources[typeof(TPAPAVM)]
                };
                view.Resources.MergedDictionaries.Add(resources);

                UpdateView(view);

                var manualPanel = Descendants<ContentPresenter>(view)
                    .Single(presenter => ReferenceEquals(presenter.Content, upas));
                var automaticModeLabel = Descendants<TextBlock>(view)
                    .Single(textBlock => textBlock.Text == "Automatic Mode");
                var automaticPanel = Ancestor<Grid>(automaticModeLabel);

                manualPanel.Visibility.Should().Be(Visibility.Visible);
                automaticPanel.Visibility.Should().Be(Visibility.Collapsed);

                PluginSettings.Default.DoAutomatedAdjustments = true;
                upas.RaiseAllPropertiesChanged();
                UpdateView(view);

                manualPanel.Visibility.Should().Be(Visibility.Collapsed,
                    "UPAS speed, gear-ratio, position and movement controls must be hidden in automation mode");
                automaticPanel.Visibility.Should().Be(Visibility.Visible);

                PluginSettings.Default.DoAutomatedAdjustments = false;
                upas.RaiseAllPropertiesChanged();
                UpdateView(view);

                manualPanel.Visibility.Should().Be(Visibility.Visible);
                automaticPanel.Visibility.Should().Be(Visibility.Collapsed);
            } finally {
                PluginSettings.Default.DoAutomatedAdjustments = originalAutomationSetting;
                SetUPAS(originalUPAS);
            }
        }

        private static void UpdateView(FrameworkElement view) {
            view.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            view.Measure(new Size(1200, 1000));
            view.Arrange(new Rect(0, 0, 1200, 1000));
            view.UpdateLayout();
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match) {
                    yield return match;
                }

                foreach (var descendant in Descendants<T>(child)) {
                    yield return descendant;
                }
            }
        }

        private static T Ancestor<T>(DependencyObject child) where T : DependencyObject {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null) {
                if (parent is T match) {
                    return match;
                }

                parent = VisualTreeHelper.GetParent(parent);
            }

            throw new AssertionException($"No {typeof(T).Name} ancestor was found.");
        }

        private static void SetUPAS(UniversalPolarAlignmentVM upas) {
            typeof(PolarAlignmentPlugin)
                .GetProperty(nameof(PolarAlignmentPlugin.UniversalPolarAlignmentVM), BindingFlags.Public | BindingFlags.Static)!
                .GetSetMethod(nonPublic: true)!
                .Invoke(null, new object[] { upas });
        }
    }
}
