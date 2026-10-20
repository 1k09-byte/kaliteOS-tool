using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using kaliteConfig.PackageManager.Models;
using kaliteConfig.PackageManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.PackageManager.Views
{
    public sealed partial class PackageListView : UserControl
    {
        public event EventHandler<PackageInfo>? ActionRequested;
        public event EventHandler<PackageInfo>? InfoRequested;
        public event EventHandler<PackageInfo>? SelectionToggled;

        public PackageListView()
        {
            this.InitializeComponent();
            ApplyLayout();
        }

        public IEnumerable? Items
        {
            get => (IEnumerable?)GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        public static readonly DependencyProperty ItemsProperty =
            DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(PackageListView),
                new PropertyMetadata(null, (d, e) =>
                {
                    var self = (PackageListView)d;
                    if (self.PackageList != null)
                        self.PackageList.ItemsSource = (IEnumerable?)e.NewValue;
                }));

        public PackageViewMode ViewMode
        {
            get => (PackageViewMode)GetValue(ViewModeProperty);
            set => SetValue(ViewModeProperty, value);
        }

        public static readonly DependencyProperty ViewModeProperty =
            DependencyProperty.Register(nameof(ViewMode), typeof(PackageViewMode), typeof(PackageListView),
                new PropertyMetadata(PackageViewMode.List, (d, _) => ((PackageListView)d).RefreshRealizedRows()));

        public string ActionLabel
        {
            get => (string)GetValue(ActionLabelProperty);
            set => SetValue(ActionLabelProperty, value);
        }

        public static readonly DependencyProperty ActionLabelProperty =
            DependencyProperty.Register(nameof(ActionLabel), typeof(string), typeof(PackageListView),
                new PropertyMetadata("", (d, _) => ((PackageListView)d).RefreshRealizedRows()));

        public bool ShowAction
        {
            get => (bool)GetValue(ShowActionProperty);
            set => SetValue(ShowActionProperty, value);
        }

        public static readonly DependencyProperty ShowActionProperty =
            DependencyProperty.Register(nameof(ShowAction), typeof(bool), typeof(PackageListView),
                new PropertyMetadata(true, (d, _) => ((PackageListView)d).RefreshRealizedRows()));

        public bool ShowInfo
        {
            get => (bool)GetValue(ShowInfoProperty);
            set => SetValue(ShowInfoProperty, value);
        }

        public static readonly DependencyProperty ShowInfoProperty =
            DependencyProperty.Register(nameof(ShowInfo), typeof(bool), typeof(PackageListView),
                new PropertyMetadata(true, (d, _) => ((PackageListView)d).RefreshRealizedRows()));

        public ICommand? ActionCommand
        {
            get => (ICommand?)GetValue(ActionCommandProperty);
            set => SetValue(ActionCommandProperty, value);
        }

        public static readonly DependencyProperty ActionCommandProperty =
            DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(PackageListView),
                new PropertyMetadata(null, (d, _) => ((PackageListView)d).RefreshRealizedRows()));

        public ICommand? CancelCommand
        {
            get => (ICommand?)GetValue(CancelCommandProperty);
            set => SetValue(CancelCommandProperty, value);
        }

        public static readonly DependencyProperty CancelCommandProperty =
            DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(PackageListView),
                new PropertyMetadata(null, (d, _) => ((PackageListView)d).RefreshRealizedRows()));

        private readonly List<PackageRow> _pushedRows = new();

        private void ApplyLayout()
        {
            if (PackageList is null) return;
            string key = ViewMode == PackageViewMode.Grid ? "GridLayoutPanel" : "ListLayoutPanel";
            if (PackageList.Resources.TryGetValue(key, out var value) && value is ItemsPanelTemplate panel)
                PackageList.ItemsPanel = panel;
        }

        private void PackageList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not PackageInfo item) return;
            if (args.ItemContainer?.Content is not PackageRow row) return;
            PushToRow(row, item);
        }

        private void OnRowForwarded(object? s, PackageInfo p) => ActionRequested?.Invoke(this, p);
        private void OnRowInfo(object? s, PackageInfo p) => InfoRequested?.Invoke(this, p);
        private void OnRowSelection(object? s, PackageInfo p) => SelectionToggled?.Invoke(this, p);

        private void PushToRow(PackageRow row, PackageInfo package)
        {
            row.ActionRequested -= OnRowForwarded;
            row.InfoRequested -= OnRowInfo;
            row.SelectionToggled -= OnRowSelection;
            row.Package = package;
            row.ViewMode = ViewMode;
            if (ActionLabel == "Install" && !string.IsNullOrWhiteSpace(package.InstalledVersion))
            {
                row.ActionLabel = package.HasUpdate ? "Update" : "Installed";
            }
            else
            {
                row.ActionLabel = ActionLabel;
            }
            row.ShowAction = ShowAction;
            row.ShowInfo = ShowInfo;
            row.ActionCommand = ActionCommand;
            row.CancelCommand = CancelCommand;
            row.ActionRequested += OnRowForwarded;
            row.InfoRequested += OnRowInfo;
            row.SelectionToggled += OnRowSelection;
            if (!_pushedRows.Contains(row)) _pushedRows.Add(row);
        }

        private void RefreshRealizedRows()
        {
            ApplyLayout();
            foreach (var row in _pushedRows.ToArray())
            {
                if (row.Package is PackageInfo item) PushToRow(row, item);
            }
        }
    }
}
