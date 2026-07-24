using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace NINA.Plugins.PolarAlignment.OAPA {
    /// <summary>
    /// Interaction logic for OAPAControlPanel.xaml
    /// </summary>
    public partial class OAPAControlPanel : UserControl {
        public OAPAControlPanel() {
            InitializeComponent();
            // DataContext is set by the parent via data binding
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e) {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
