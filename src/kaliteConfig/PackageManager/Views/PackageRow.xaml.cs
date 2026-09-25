using System;
using System.Windows.Input;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.PackageManager.Views
{
    public sealed partial class PackageRow : UserControl, System.ComponentModel.INotifyPropertyChanged
    {
        public event EventHandler<PackageInfo>? ActionRequested;
        public event EventHandler<PackageInfo>? InfoRequested;
        public event EventHandler<PackageInfo>? SelectionToggled;

        public PackageRow()
        {
            this.InitializeComponent();
            ApplyViewMode();
        }

        public PackageInfo? Package
        {
            get => (PackageInfo?)GetValue(PackageProperty);
            set => SetValue(PackageProperty, value);
        }

        public static readonly DependencyProperty PackageProperty =
            DependencyProperty.Register(nameof(Package), typeof(PackageInfo), typeof(PackageRow),
                new PropertyMetadata(null, (d, _) => ((PackageRow)d).OnPackageChanged()));

        public PackageViewMode ViewMode
        {
            get => (PackageViewMode)GetValue(ViewModeProperty);
            set => SetValue(ViewModeProperty, value);
        }

        public static readonly DependencyProperty ViewModeProperty =
            DependencyProperty.Register(nameof(ViewMode), typeof(PackageViewMode), typeof(PackageRow),
                new PropertyMetadata(PackageViewMode.List, (d, _) => ((PackageRow)d).ApplyViewMode()));

        public string ActionLabel
        {
            get => (string)GetValue(ActionLabelProperty);
            set => SetValue(ActionLabelProperty, value);
        }

        public static readonly DependencyProperty ActionLabelProperty =
            DependencyProperty.Register(nameof(ActionLabel), typeof(string), typeof(PackageRow),
                new PropertyMetadata(""));

        public bool ShowAction
        {
            get => (bool)GetValue(ShowActionProperty);
            set => SetValue(ShowActionProperty, value);
        }

        public static readonly DependencyProperty ShowActionProperty =
            DependencyProperty.Register(nameof(ShowAction), typeof(bool), typeof(PackageRow),
                new PropertyMetadata(true));

        public bool ShowInfo
        {
            get => (bool)GetValue(ShowInfoProperty);
            set => SetValue(ShowInfoProperty, value);
        }

        public static readonly DependencyProperty ShowInfoProperty =
            DependencyProperty.Register(nameof(ShowInfo), typeof(bool), typeof(PackageRow),
                new PropertyMetadata(true));

        public ICommand? ActionCommand
        {
            get => (ICommand?)GetValue(ActionCommandProperty);
            set => SetValue(ActionCommandProperty, value);
        }

        public static readonly DependencyProperty ActionCommandProperty =
            DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(PackageRow),
                new PropertyMetadata(null));

        public ICommand? CancelCommand
        {
            get => (ICommand?)GetValue(CancelCommandProperty);
            set => SetValue(CancelCommandProperty, value);
        }

        public static readonly DependencyProperty CancelCommandProperty =
            DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(PackageRow),
                new PropertyMetadata(null));

        // Avatar initial + x:Bind static helpers (no converters needed).
        public string Initial
        {
            get
            {
                var name = Package?.DisplayName ?? "?";
                return name.Length == 0 ? "?" : name.Substring(0, 1).ToUpperInvariant();
            }
        }

        public static Visibility BoolToVis(bool value) =>
            value ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility RunningVis(PackageOperationState state) =>
            state == PackageOperationState.Running ? Visibility.Visible : Visibility.Collapsed;

        private void OnPackageChanged()
        {
            // Re-evaluate Initial + every Package.* binding.
            OnPropertyChanged(nameof(Initial));
        }

        private void ApplyViewMode()
        {
            if (ListWrap is null) return;
            ListWrap.Visibility = ViewMode == PackageViewMode.List ? Visibility.Visible : Visibility.Collapsed;
            CompactGrid.Visibility = ViewMode == PackageViewMode.Compact ? Visibility.Visible : Visibility.Collapsed;
            CardBorder.Visibility = ViewMode == PackageViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SelectionBox_Toggled(object sender, RoutedEventArgs e)
        {
            if (Package != null) SelectionToggled?.Invoke(this, Package);
        }

        /// <summary>
        /// Fires ActionRequested only when no ActionCommand is bound (pages
        /// that need a confirmation step first leave the command unset and
        /// handle this event instead - e.g. uninstall).
        /// </summary>
        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ActionCommand is null && Package != null)
                ActionRequested?.Invoke(this, Package);
        }

        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (Package != null) InfoRequested?.Invoke(this, Package);
        }

        // Minimal INPC for the Initial projection (DependencyObject has none).
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
