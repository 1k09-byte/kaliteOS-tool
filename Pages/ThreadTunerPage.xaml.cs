using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using kaliteConfig.Models;
using kaliteConfig.Services;
using kaliteConfig.ViewModels;
using kaliteConfig.Controls;
using kaliteConfig.ProcessOptimizer.ViewModels;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;

namespace kaliteConfig.Pages
{
    public sealed partial class ThreadTunerPage : Page
    {
        public ThreadTunerViewModel ViewModel { get; }
        private readonly kaliteConfig.Services.StartupService _startup = new();
        private bool _syncingStartupToggle;
        private TunerProcessRow? _contextProcessRow;

        public ThreadTunerPage()
        {
            this.InitializeComponent();
            this.ViewModel = new ThreadTunerViewModel();
            this.DataContext = ViewModel;
            
            this.Loaded += ThreadTunerPage_Loaded;
        }

        public static Microsoft.UI.Xaml.Media.Brush RowNameBrush(bool isProtected)
        {
            var resources = Application.Current.Resources;
            return (Microsoft.UI.Xaml.Media.Brush)(isProtected
                ? resources["SystemFillColorCriticalBrush"]
                : resources["TextFillColorPrimaryBrush"]);
        }

        /// <summary>x:Bind helper for the per-process boost tick box.</summary>
        public static bool Not(bool value) => !value;

        private void ThreadTunerPage_Loaded(object sender, RoutedEventArgs e)
        {
            ViewModel.LoadProcessesCommand.Execute(null);
            HookSuspendUi();
            
            App.Current.ProfileWatcher.RulesChanged += (_, _) =>
            {
                RefreshPageGamingModeUi();
                PageGamingStatusText.Text = App.Current.GamingMode.IsActive
                    ? "Gaming mode ON (automatic · from a rule)"
                    : string.Empty;
            };
            _ = SyncStartupToggleAsync();
        }

        private async Task SyncStartupToggleAsync()
        {
            _syncingStartupToggle = true;
            try { StartupToggle.IsOn = await _startup.IsEnabledAsync(); }
            catch { StartupToggle.IsOn = false; }
            finally { _syncingStartupToggle = false; }
        }

        private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingStartupToggle) return;
            _syncingStartupToggle = true;
            try
            {
                bool ok = await _startup.SetEnabledAsync(StartupToggle.IsOn);
                if (!ok) StartupToggle.IsOn = !StartupToggle.IsOn;
            }
            catch { StartupToggle.IsOn = !StartupToggle.IsOn; }
            finally { _syncingStartupToggle = false; }
        }

        private TunerProcessRow? GetRow(object sender) => (sender as FrameworkElement)?.DataContext as TunerProcessRow;

        private TunerProcessRow? SelectedProcess =>
            (ProcessesGrid.SelectedItem as TunerProcessRow)
            ?? (ProcessesGrid.ItemsSource as System.Collections.Generic.IEnumerable<TunerProcessRow>)?.FirstOrDefault(p => p.Pid == _lastSelectedPid);

        private int _lastSelectedPid = -1;

        private void ProcessesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProcessesGrid.SelectedItem is TunerProcessRow row)
                _lastSelectedPid = row.Pid;
            RefreshPageGamingModeUi();
        }

        private void RefreshPageGamingModeUi()
        {
            var svc = App.Current.GamingMode;
            bool hasTarget = SelectedProcess is not null;

            PageGamingModeButton.Content = svc.IsActive ? "Turn off Gaming mode" : "Enable Gaming mode";
            PageGamingModeButton.IsEnabled = true;
            PageRestoreButton.IsEnabled = svc.RestorableCount > 0;

            string target = SelectedProcess is { } r ? r.Name : "(no process selected)";
            PageGamingStatusText.Text = svc.IsActive
                ? $"Gaming mode ON · target {target} · {svc.RestorableCount} restorable"
                : hasTarget
                    ? $"Ready · target {target}"
                    : "Select a process row, or use a rule with Automatic Gaming mode";
        }

        private async void Page_ToggleGamingMode(object sender, RoutedEventArgs e)
        {
            var svc = App.Current.GamingMode;
            PageGamingModeButton.IsEnabled = false;
            try
            {
                if (!svc.IsActive)
                {
                    if (SelectedProcess is not { } row)
                    {
                        PageGamingStatusText.Text = "Click a process row first, or create a rule with 'Automatic Gaming mode' ticked";
                        return;
                    }

                    var result = await svc.ActivateAsync(row.Pid);
                    PageGamingStatusText.Text = result.Summary;
                }
                else
                {
                    svc.Deactivate();
                    PageGamingStatusText.Text = "Gaming mode off · priorities restored";
                }
            }
            catch (Exception ex)
            {
                PageGamingStatusText.Text = $"Gaming mode error: {ex.Message}";
            }
            finally
            {
                RefreshPageGamingModeUi();
            }
        }

        private async void Page_RestorePriorities(object sender, RoutedEventArgs e)
        {
            App.Current.GamingMode.Deactivate();
            PageGamingStatusText.Text = "Priorities restored";
            RefreshPageGamingModeUi();

            // Also normalize everything else the app/other tools changed.
            var dialog = new ContentDialog
            {
                Title = "Normalize all priorities?",
                Content = new TextBlock
                {
                    Text = "Set every accessible process's priority back to Normal?\n\nThis is a full reset — anything (including this app or other tools) that raised or lowered a priority gets set to Normal. Critical and protected system processes are skipped.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Reset all to Normal",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var (reset, skipped) = App.Current.GamingMode.ResetAllPrioritiesToNormal();
                PageGamingStatusText.Text = $"Reset {reset} process(es) to Normal ({skipped} skipped).";
                RefreshPageGamingModeUi();
            }
        }

        // ─── Suspend mode (image-3 style: freeze background, resume on switch-back) ──
        private bool _suspendHooked;

        private void HookSuspendUi()
        {
            if (_suspendHooked) return;
            _suspendHooked = true;
            try { App.Current.ForegroundSuspend.StatusChanged += SuspendStatus_Changed; } catch { }
            RefreshSuspendUi();
        }

        private void SuspendStatus_Changed()
        {
            DispatcherQueue.TryEnqueue(RefreshSuspendUi);
        }

        private void RefreshSuspendUi()
        {
            try
            {
                var svc = App.Current.ForegroundSuspend;
                PageSuspendStatusText.Text =
                    $"[{svc.SuspendedCount} suspended] {svc.StatusText}";
            }
            catch { }
        }

        // Suspend-mode controls (Resume all / Auto / Add game / Games…) were
        // removed from the page: the service still tracks state (its status and
        // suspended count stay visible above, and MainWindow resumes everything
        // it suspended on exit), but the page no longer drives it.

        private async void ExecuteTuning(object sender, Func<TunerProcessRow, Task> action)
        {
            if (GetRow(sender) is { } row)
            {
                try { await action(row); }
                catch (Exception ex)
                {
                    _ = new ContentDialog
                    {
                        Title = "Execution Blocked",
                        Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                        CloseButtonText = "Close",
                        XamlRoot = this.XamlRoot
                    }.ShowAsync();
                }
            }
        }

        private void ProcessMenu_Opening(object sender, object e)
        {
            if (sender is MenuFlyout menu && menu.Target is FrameworkElement target && target.DataContext is TunerProcessRow row)
            {
                _contextProcessRow = row;
                _ = UpdateProcessMenuStateAsync(menu, row);
            }
        }

        private TunerProcessRow? GetContextProcessRow(object sender) => GetRow(sender) ?? _contextProcessRow;

        private async Task UpdateProcessMenuStateAsync(MenuFlyout menu, TunerProcessRow row)
        {
            // Disable mutating actions if process is protected
            foreach (var item in menu.Items)
            {
                string text = (item as MenuFlyoutItem)?.Text ?? (item as MenuFlyoutSubItem)?.Text ?? "";
                if (text.Contains("Terminate") || text.Contains("Suspend") || text.Contains("Resume") || 
                    text.Contains("Priority") || text.Contains("Affinity") || 
                    text.Contains("boost") || text.Contains("Efficiency"))
                {
                    if (item is Control c) c.IsEnabled = !row.IsProtected;
                }
            }

            var priority = menu.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(x => x.Text.StartsWith("Priority", StringComparison.OrdinalIgnoreCase));
            if (priority != null)
            {
                priority.Text = $"Priority  (current: {row.PriorityText})";
                MarkMenuChoice(priority, row.PriorityText);
            }

            var affinity = menu.Items.OfType<MenuFlyoutItem>()
                .FirstOrDefault(x => x.Text.StartsWith("CPU affinity", StringComparison.OrdinalIgnoreCase));
            if (affinity != null)
            {
                affinity.Text = $"CPU affinity... (current: {row.AffinitySummary})";
            }

            try
            {
                bool boostEnabled = await App.Current.ProcessTuning.GetBoostAsync(row.Pid);
                var boost = menu.Items.OfType<MenuFlyoutSubItem>()
                    .FirstOrDefault(x => x.Text.StartsWith("Priority boost", StringComparison.OrdinalIgnoreCase));
                if (boost != null)
                {
                    boost.Text = $"Priority boost  (current: {(boostEnabled ? "Enabled" : "Disabled")})";
                    MarkMenuChoice(boost, boostEnabled ? "Enabled" : "Disabled");
                }
            }
            catch { }

            try
            {
                bool efficiencyEnabled = await App.Current.ProcessTuning.GetEfficiencyAsync(row.Pid);
                var efficiency = menu.Items.OfType<MenuFlyoutSubItem>()
                    .FirstOrDefault(x => x.Text.StartsWith("Efficiency mode", StringComparison.OrdinalIgnoreCase));
                if (efficiency != null)
                {
                    efficiency.Text = $"Efficiency mode  (current: {(efficiencyEnabled ? "Enabled" : "Disabled")})";
                    MarkMenuChoice(efficiency, efficiencyEnabled ? "Enabled" : "Disabled");
                }
            }
            catch { }
        }

        private static void MarkMenuChoice(MenuFlyoutSubItem menu, string current)
        {
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
            {
                string label = item.Tag?.ToString() switch
                {
                    "AboveNormal" => "Above normal",
                    "BelowNormal" => "Below normal",
                    _ => item.Tag?.ToString() ?? item.Text.Replace(" ✓", "")
                };
                item.Text = string.Equals(label, current, StringComparison.OrdinalIgnoreCase)
                    ? $"{label}  ✓"
                    : label;
            }
        }

        private async void Action_ShowSettings(object sender, RoutedEventArgs e)
        {
            if (GetContextProcessRow(sender) is not { } row) return;
            try
            {
                ulong affinity = await App.Current.ProcessTuning.GetAffinityAsync(row.Pid);
                bool boost = await App.Current.ProcessTuning.GetBoostAsync(row.Pid);
                bool efficiency = await App.Current.ProcessTuning.GetEfficiencyAsync(row.Pid);

                await new ContentDialog
                {
                    Title = $"Current settings — {row.Name}",
                    Content = new TextBlock
                    {
                        Text = $"PID: {row.Pid}\nAffinity mask: 0x{affinity:X}\nCPUs: {FormatCpuMask(affinity)}\nPriority boost: {(boost ? "Enabled" : "Disabled")}\nEfficiency mode: {(efficiency ? "Enabled" : "Disabled")}",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CloseButtonText = "Close",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                await new ContentDialog
                {
                    Title = "Could not read settings",
                    Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "Close",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
            }
        }

        private static string FormatCpuMask(ulong mask)
        {
            var cpus = new List<string>();
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) != 0) cpus.Add(i.ToString());
            }
            return cpus.Count == 0 ? "None" : string.Join(", ", cpus);
        }

        private void Action_Terminate(object sender, RoutedEventArgs e) => ExecuteTuning(sender, row => App.Current.ProcessTuning.TerminateProcessAsync(row.Pid));
        private void Action_Suspend(object sender, RoutedEventArgs e) => ExecuteTuning(sender, row => App.Current.ProcessTuning.SuspendProcessAsync(row.Pid));
        private void Action_Resume(object sender, RoutedEventArgs e) => ExecuteTuning(sender, row => App.Current.ProcessTuning.ResumeProcessAsync(row.Pid));

        private void Action_Priority(object sender, RoutedEventArgs e) => ExecuteTuning(sender, async row =>
        {
            if (sender is FrameworkElement { Tag: string p })
            {
                uint priorityClass = p switch
                {
                    "Realtime" => 0x00000100,
                    "High" => 0x00000080,
                    "AboveNormal" => 0x00008000,
                    "Normal" => 0x00000020,
                    "BelowNormal" => 0x00004000,
                    "Idle" => 0x00000040,
                    _ => 0x00000020
                };
                await App.Current.ProcessTuning.SetPriorityAsync(row.Pid, priorityClass);
            }
        });

        private void Action_Eco(object sender, RoutedEventArgs e) => ExecuteTuning(sender, async row =>
        {
            if (sender is FrameworkElement { Tag: string eco })
            {
                await App.Current.ProcessTuning.SetEfficiencyAsync(row.Pid, eco == "Enabled");
            }
        });

        private void Action_Affinity(object sender, RoutedEventArgs e)
        {
            if (GetContextProcessRow(sender) is not { } row) return;
            _ = ShowProcessAffinityAsync(row);
        }

        private async Task ShowProcessAffinityAsync(TunerProcessRow row)
        {
            int cpus = App.Current.ProcessTuning.LogicalProcessorCount();
            ulong currentMask = await App.Current.ProcessTuning.GetAffinityAsync(row.Pid);

            var panel = new StackPanel { Spacing = 8 };
            var checkboxes = new List<CheckBox>();

            for (int i = 0; i < cpus; i++)
            {
                bool isSet = (currentMask & (1UL << i)) != 0;
                var cb = new CheckBox { Content = $"CPU {i}", IsChecked = isSet, Tag = i };
                checkboxes.Add(cb);
                panel.Children.Add(cb);
            }

            var dialog = new ContentDialog
            {
                Title = $"Processor Affinity: {row.Name} (current: 0x{currentMask:X})",
                Content = new ScrollViewer { Content = panel, MaxHeight = 400 },
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ulong newMask = 0;
                foreach (var cb in checkboxes)
                {
                    if (cb.IsChecked == true && cb.Tag is int bit) newMask |= (1UL << bit);
                }
                if (newMask != 0) await App.Current.ProcessTuning.SetAffinityAsync(row.Pid, newMask);
            }
        }

        private void Action_Boost(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag)
            {
                bool enabled = tag != "Disabled";
                // Same path as the tick box column: apply to the live process and
                // remember the choice, so the menu action is permanent too.
                ExecuteTuning(sender, row => SetProcessBoostAsync(row, enabled));
            }
        }

        /// <summary>
        /// Per-process Priority boost: writes the live process and records the
        /// choice, which the watcher re-applies on every launch and the keeper
        /// sweep re-applies while the process runs. Without the stored
        /// preference this was a one-shot Windows forgot on the next restart.
        /// </summary>
        private async Task SetProcessBoostAsync(TunerProcessRow row, bool enabled)
        {
            int applied = await Services.ProcessBoostPreferenceService.Instance.SetAsync(row.Name, enabled);
            row.BoostAllowed = enabled;
            row.PriorityBoostText = enabled ? "Enabled" : "Disabled";

            if (applied == 0)
            {
                // Recorded, but nothing was running to write it to (or Windows
                // refused every instance) — say so instead of pretending.
                _ = new ContentDialog
                {
                    Title = "Boost preference saved",
                    Content = new TextBlock
                    {
                        Text = $"No running instance of {row.Name} accepted the change. " +
                               "The preference is saved and will be applied the next time it starts.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    CloseButtonText = "Close",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
            }
        }

        private async void RowBoost_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox box || box.Tag is not TunerProcessRow row) return;
            bool enabled = box.IsChecked == true;

            try
            {
                await SetProcessBoostAsync(row, enabled);
            }
            catch (Exception ex)
            {
                // Put the tick back where it was: the write did not happen.
                row.BoostAllowed = !enabled;
                await new ContentDialog
                {
                    Title = "Priority boost",
                    Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "Close",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
            }
        }

        private void Action_Threads(object sender, RoutedEventArgs? e) => ExecuteTuning(sender, row => ShowThreadsDialogAsync(row));

        private async Task ShowThreadsDialogAsync(Models.TunerProcessRow row)
        {
            var dialog = new Controls.ThreadListDialog();
            await dialog.ShowForProcessAsync(row.Pid, row.Name, this.XamlRoot);
        }
        
        private void Row_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => Action_Threads(sender, null);

        private async Task OpenRuleEditorAsync(TunerProfile target, bool isNew)
        {
            var dlg = new RuleEditorDialog { XamlRoot = this.XamlRoot };
            dlg.LoadFrom(target, isNew);
            if (await dlg.ShowAsync() == ContentDialogResult.Primary || dlg.ShortcutAccepted)
            {
                target.CopyFrom(dlg.Draft);
                await App.Current.ProfileWatcher.AddOrUpdate(target);
                await App.Current.ProfileWatcher.ApplyProfileNowAsync(target);
            }
        }

        private void Action_CreateRule(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TunerProcessRow row)
            {
                var draft = new TunerProfile
                {
                    Name = row.Name, Pattern = row.Name, AutoApply = true, Enabled = true
                };
                _ = OpenRuleEditorAsync(draft, isNew: true);
            }
        }

        private void Action_Details(object sender, RoutedEventArgs e)
        {
            ExecuteTuning(sender, async row =>
            {
                using var proc = System.Diagnostics.Process.GetProcessById(row.Pid);
                var panel = new StackPanel { Spacing = 12 };
                
                string path = "Access Denied";
                try { path = proc.MainModule?.FileName ?? "Unknown"; } catch { }

                panel.Children.Add(new TextBlock { Text = $"Executable Path:\n{path}", TextWrapping = TextWrapping.Wrap, Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"] });
                panel.Children.Add(new TextBlock { Text = $"Handles: {proc.HandleCount}", Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"] });
                panel.Children.Add(new TextBlock { Text = $"Working Set (RAM): {(proc.WorkingSet64 / 1024 / 1024.0):F1} MB", Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"] });
                try { panel.Children.Add(new TextBlock { Text = $"Start Time: {proc.StartTime}", Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyTextBlockStyle"] }); } catch { }

                await new ContentDialog
                {
                    Title = $"Details: {row.Name} ({row.Pid})",
                    Content = panel,
                    CloseButtonText = "Close",
                    XamlRoot = this.XamlRoot
                }.ShowAsync();
            });
        }
        private void MainSelectorBar_SelectionChanged(Microsoft.UI.Xaml.Controls.SelectorBar sender, Microsoft.UI.Xaml.Controls.SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem == TabRules)
            {
                ProcessesPanel.Visibility = Visibility.Collapsed;
                RulesPanel.Visibility = Visibility.Visible;
                ViewModel.SyncProfiles();
                RefreshRulesMeta();
            }
            else
            {
                RulesPanel.Visibility = Visibility.Collapsed;
                ProcessesPanel.Visibility = Visibility.Visible;
            }
        }

        private void RefreshRulesMeta()
        {
            int n = ViewModel.DisplayedProcessProfiles.Count;
            RulesCountText.Text = n == 1 ? "1 rule" : $"{n} rules";
        }

        private TunerProfile? GetRuleRow(object sender) =>
            (sender as FrameworkElement)?.DataContext as TunerProfile;

        private TunerProfile? SelectedRuleOrRow(object sender) =>
            ViewModel.SelectedProfile ?? GetRuleRow(sender);

        private async void RuleAdd_Click(object sender, RoutedEventArgs e) =>
            await OpenRuleEditorAsync(new TunerProfile(), isNew: true);

        private async void RulePresets_Click(object sender, RoutedEventArgs e)
        {
            await ShowRulesErrorAsync("Gaming presets were removed in Phase 4.");
        }

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
    }
}
