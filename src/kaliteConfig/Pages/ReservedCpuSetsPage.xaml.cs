using Microsoft.UI.Xaml.Controls;
using kaliteConfig.ViewModels;

namespace kaliteConfig.Pages
{
    public sealed partial class ReservedCpuSetsPage : Page
    {
        public ReservedCpuSetsViewModel ViewModel { get; } = new();

        public ReservedCpuSetsPage()
        {
            this.InitializeComponent();
            this.DataContext = ViewModel;
        }
    }
}
