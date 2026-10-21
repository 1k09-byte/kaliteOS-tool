using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.GpuOverclock.Views
{
    public sealed partial class AmdGpuSection : UserControl
    {
        public AmdGpuSection()
        {
            this.InitializeComponent();
            this.Loaded += async (s, e) =>
            {
                await Vm.EnsureLoadedAsync();
                Visibility = Vm.ShouldShow ? Visibility.Visible : Visibility.Collapsed;
            };
        }
    }
}
