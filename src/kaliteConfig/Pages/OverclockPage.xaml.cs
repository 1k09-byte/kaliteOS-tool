using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.Pages
{
    public sealed partial class OverclockPage : Page
    {
        public OverclockPage()
        {
            this.InitializeComponent();
        }

        private async void RedetectBtn_Click(object sender, RoutedEventArgs e)
        {
            try { await OverclockPanel.Vm.RefreshAsync(); }
            catch { }
        }
    }
}
