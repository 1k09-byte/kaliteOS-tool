using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using kaliteConfig.ViewModels;

namespace kaliteConfig.Pages
{
    public sealed partial class EtwManagerPage : Page
    {
        public EtwManagerViewModel ViewModel { get; }

        // Guard flag: suppresses toggle handlers while the list is being
        // populated so that data-binding initialization doesn't fire
        // OnSessionToggled / OnAutoLoggerToggled for every single row.
        private bool _suppressToggleEvents;

        public EtwManagerPage()
        {
            this.InitializeComponent();
            ViewModel = new EtwManagerViewModel();
            this.DataContext = ViewModel;

            // Subscribe to the ViewModel's refresh guard so we know when
            // to suppress and re-enable toggle event handling.
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModel.IsRefreshing))
                    _suppressToggleEvents = ViewModel.IsRefreshing;
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _suppressToggleEvents = true;
            ViewModel.RefreshCommand.Execute(null);
        }

        private async void OnSessionToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_suppressToggleEvents) return;
            if (sender is ToggleSwitch ts && ts.DataContext is kaliteConfig.Models.EtwSessionModel model)
            {
                // The TwoWay binding already flipped model.IsRunning.
                if (!ts.IsOn)
                {
                    _suppressToggleEvents = true;
                    await ViewModel.StopSessionAsync(model);
                    _suppressToggleEvents = false;
                }
                else
                {
                    _suppressToggleEvents = true;
                    await ViewModel.StartSessionAsync(model);
                    _suppressToggleEvents = false;
                }
            }
        }

        private void OnAutoLoggerToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_suppressToggleEvents) return;
            if (sender is ToggleSwitch ts && ts.DataContext is kaliteConfig.Models.EtwSessionModel model)
            {
                _suppressToggleEvents = true;
                ViewModel.ToggleAutoLogger(model, ts.IsOn);
                _suppressToggleEvents = false;
            }
        }
    }
}
