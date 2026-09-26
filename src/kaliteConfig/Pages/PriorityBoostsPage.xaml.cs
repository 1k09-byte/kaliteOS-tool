// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.Controls;
using kaliteConfig.ViewModels;

namespace kaliteConfig.Pages
{
    public sealed partial class PriorityBoostsPage : Page
    {
        public ThreadTunerViewModel ViewModel { get; }

        public PriorityBoostsPage()
        {
            this.InitializeComponent();
            this.ViewModel = new ThreadTunerViewModel();
            this.DataContext = ViewModel;

            this.Loaded += Page_Loaded;
            this.Unloaded += Page_Unloaded;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (MainSelectorBar.SelectedItem == null)
            {
                MainSelectorBar.SelectedItem = TabThreads;
            }

            ViewModel.SyncProfiles();
            RefreshRulesMeta();
            App.Current.ProfileWatcher.RulesChanged += ProfileWatcher_RulesChanged;
        }

        private void ProfileWatcher_RulesChanged(object? sender, EventArgs e)
        {
            RefreshRulesMeta();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            App.Current.ProfileWatcher.RulesChanged -= ProfileWatcher_RulesChanged;
        }

        private void MainSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            ViewModel.IsThreadTuneTabActive = sender.SelectedItem != TabRules;

            if (sender.SelectedItem == TabRules)
            {
                ThreadsPanel.Visibility = Visibility.Collapsed;
                RulesPanel.Visibility = Visibility.Visible;
                ViewModel.SyncProfiles();
                RefreshRulesMeta();
            }
            else
            {
                RulesPanel.Visibility = Visibility.Collapsed;
                ThreadsPanel.Visibility = Visibility.Visible;
                _ = ViewModel.LoadThreadBoostRowsAsync();
            }
        }

    // --- Rules ---

        private void RefreshRulesMeta()
        {
            int n = ViewModel.DisplayedThreadProfiles.Count;
            RulesCountText.Text = n == 1 ? "1 rule" : $"{n} rules";
        }

        private TunerProfile? GetRuleRow(object sender) =>
            (sender as FrameworkElement)?.DataContext as TunerProfile;

        private TunerProfile? SelectedRuleOrRow(object sender) =>
            ViewModel.SelectedProfile ?? GetRuleRow(sender);

        private async Task OpenRuleEditorAsync(TunerProfile target, bool isNew)
        {
            var dlg = new RuleEditorDialog { XamlRoot = this.XamlRoot };
            dlg.LoadFrom(target, isNew);
            if (await dlg.ShowAsync() == ContentDialogResult.Primary || dlg.ShortcutAccepted)
            {
                target.CopyFrom(dlg.Draft);
                bool saved = await App.Current.ProfileWatcher.AddOrUpdate(target);
                ViewModel.SyncProfiles();
                if (!saved)
                {
                    await ShowRulesErrorAsync("Could not save the rule file. The rule is active for this session only.");
                }
                await App.Current.ProfileWatcher.ApplyProfileNowAsync(target);
                ViewModel.SyncProfiles();
                RefreshRulesMeta();
            }
        }

        private async void RuleAdd_Click(object sender, RoutedEventArgs e) =>
            await OpenRuleEditorAsync(new TunerProfile(), isNew: true);

        private Task ShowRulesErrorAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Rules",
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close",
                XamlRoot = this.XamlRoot,
            };
            return dialog.ShowAsync().AsTask();
        }

        private async void RuleEdit_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRuleOrRow(sender) is { } p)
            {
                await OpenRuleEditorAsync(p, isNew: false);
            }
        }

        private async void RuleToggle_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRuleOrRow(sender) is not { } p) return;
            p.Enabled = !p.Enabled;
            p.RefreshSummaries();
            bool saved = await App.Current.ProfileWatcher.AddOrUpdate(p);
            ViewModel.SyncProfiles();
            if (!saved)
            {
                await ShowRulesErrorAsync("Could not save the rule file. The change is active for this session only.");
            }
            if (p.Enabled)
            {
                await App.Current.ProfileWatcher.ApplyProfileNowAsync(p);
            }
            ViewModel.SyncProfiles();
            RefreshRulesMeta();
        }

        private async void RuleDelete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRuleOrRow(sender) is not { } p) return;
            bool saved = await App.Current.ProfileWatcher.Remove(p);
            ViewModel.SyncProfiles();
            RefreshRulesMeta();
            if (!saved)
            {
                await ShowRulesErrorAsync("Could not save the rule file. The rule will reappear on restart.");
            }
        }

        private async void RuleApplyNow_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRuleOrRow(sender) is not { } p) return;
            await App.Current.ProfileWatcher.ApplyProfileNowAsync(p);
            ViewModel.SyncProfiles();
            RefreshRulesMeta();
        }

        private void RuleRow_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;
            if (GetRuleRow(sender) is { } p)
            {
                _ = OpenRuleEditorAsync(p, isNew: false);
            }
        }

        // --- Thread Tune ---

        public static bool NotProtected(bool isProtected) => !isProtected;
        public static string BoostText(bool boostEnabled) => boostEnabled ? "On" : "Off";

        private async void ThreadAction_ToggleBoost(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ThreadBoostRow row && !row.IsBusy)
            {
                row.IsBusy = true;
                bool targetState = !row.BoostEnabled; // We want to toggle it to the opposite
                try
                {
                    if (targetState)
                        await Services.BoostPreferenceService.Instance.RestoreAsync(row.ProcessName, row.Tid, row.Description, string.Empty);
                    else
                        await Services.BoostPreferenceService.Instance.SuppressAsync(row.ProcessName, row.Tid, row.Description, string.Empty);
                    
                    row.BoostEnabled = targetState;
                }
                catch (Exception ex)
                {
                    _ = new ContentDialog
                    {
                        Title = "Priority Boost",
                        Content = new TextBlock { Text = $"Could not change boost:\n{ex.Message}", TextWrapping = TextWrapping.Wrap },
                        CloseButtonText = "Close",
                        XamlRoot = this.XamlRoot,
                    }.ShowAsync();
                }
                finally
                {
                    row.IsBusy = false;
                }
            }
        }

        private void ThreadAction_CreateRule(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ThreadBoostRow row)
            {
                var draft = new TunerProfile
                {
                    Name = row.ProcessName,
                    Pattern = row.ProcessName,
                    AutoApply = true,
                    Enabled = true
                };
                
                // If it's an unnamed thread and the user clicked it, we must match it somehow
                // For unnamed threads, we'll try to match all unnamed threads unless there's an address
                draft.ThreadRules.Add(new TunerThreadRule
                {
                    Description = row.Description == "(unnamed)" ? "" : row.Description,
                    MatchAllThreads = row.Description == "(unnamed)",
                    BoostEnabled = false // common tunable target
                });

                _ = OpenRuleEditorAsync(draft, isNew: true);
            }
        }
    }
}
