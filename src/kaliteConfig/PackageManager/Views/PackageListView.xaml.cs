// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections;
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
                    if (self.Repeater != null)
                        self.Repeater.ItemsSource = (IEnumerable?)e.NewValue;
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

        private void ApplyLayout()
        {
            if (Repeater is null) return;
            if (ViewMode == PackageViewMode.Grid)
            {
                Repeater.Layout = new UniformGridLayout
                {
                    MinItemWidth = 240,
                    MinColumnSpacing = 12,
                    MinRowSpacing = 12,
                    Orientation = Orientation.Horizontal,
                };
            }
            else
            {
                Repeater.Layout = new Microsoft.UI.Xaml.Controls.StackLayout
                {
                    Orientation = Orientation.Vertical,
                    Spacing = 0,
                };
            }
        }

        private void Repeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (args.Element is PackageRow row)
            {
                if (sender.ItemsSourceView?.GetAt(args.Index) is PackageInfo item)
                    PushToRow(row, item);
            }
        }

        private void OnRowForwarded(object? s, PackageInfo p) => ActionRequested?.Invoke(this, p);
        private void OnRowInfo(object? s, PackageInfo p) => InfoRequested?.Invoke(this, p);
        private void OnRowSelection(object? s, PackageInfo p) => SelectionToggled?.Invoke(this, p);

        private void PushToRow(PackageRow row, PackageInfo package)
        {
            // Idempotent (un)subscribe: ElementPrepared also fires on recycle,
            // and these are stable instance-method references.
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
            row.ActionCommand = ActionCommand;
            row.CancelCommand = CancelCommand;
            row.ActionRequested += OnRowForwarded;
            row.InfoRequested += OnRowInfo;
            row.SelectionToggled += OnRowSelection;
        }

        private void RefreshRealizedRows()
        {
            ApplyLayout();
            if (Repeater is null || Repeater.ItemsSourceView is null) return;
            // Push control state into every realized row (recycled rows keep
            // showing the ViewMode they were created with otherwise).
            for (int i = 0; i < Repeater.ItemsSourceView.Count; i++)
            {
                if (Repeater.TryGetElement(i) is PackageRow row &&
                    Repeater.ItemsSourceView.GetAt(i) is PackageInfo item)
                {
                    PushToRow(row, item);
                }
            }
        }
    }
}
