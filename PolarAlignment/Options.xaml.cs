using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.PolarAlignment {
    [Export(typeof(ResourceDictionary))]
    partial class Options : ResourceDictionary {
        public Options() {
            InitializeComponent();
        }
    }
}
