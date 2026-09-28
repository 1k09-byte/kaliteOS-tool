using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using kaliteConfig.Native;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.Controls;

public sealed partial class ThreadRow : ObservableObject
{
    [ObservableProperty]
    public partial int Tid { get; set; }
    [ObservableProperty]
    public partial string CurrentText { get; set; } = "-";
    [ObservableProperty]
    public partial int CurrentLevel { get; set; } = int.MinValue;
    [ObservableProperty]
    public partial int Base { get; set; }
    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool IsNamed { get; set; }
    [ObservableProperty]
    public partial string StartAddress { get; set; } = string.Empty;
    [ObservableProperty]
    public partial bool CanEdit { get; set; } = true;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool Suspended { get; set; }

    public string StateText => Suspended ? "Suspended" : "Running";

    [ObservableProperty]
    public partial bool BoostAllowed { get; set; } = true;
    public string ProcessName { get; set; } = string.Empty;
    public string StartAddressValue { get; set; } = string.Empty;
}

public sealed partial class ThreadListWindow : Window
{
    public ObservableCollection<ThreadRow> Rows { get; } = new();

    private readonly int _pid;
    private readonly string _processName;
    private bool _loadingList;

    private Services.ThreadTuningService Tuner => App.Current.ThreadTuning;

    public ThreadListWindow(int pid, string processName)
    {
        InitializeComponent();
        _pid = pid;
        _processName = processName;
        Title = $"Threads - {processName} ({pid})";
        
        // Window UI configurations
        this.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 650));
        
        try
        {
            this.SystemBackdrop = new MicaBackdrop();
            var titleBar = this.AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        catch { }
        
        ThreadList.ItemsSource = Rows;

        _ = LoadThreadsAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await LoadThreadsAsync();

    private async Task LoadThreadsAsync()
    {
        _loadingList = true;
        try
        {
            Rows.Clear();
            EditorStatus("Loading threads…", false);
            try
            {
                var threads = await Services.ThreadQueryService.ListThreadsAsync(_pid);
                bool anyLocked = false;
                
                foreach (var t in threads
                    .OrderByDescending(t => !string.IsNullOrWhiteSpace(t.Description) && t.Description != "(unnamed)")
                    .ThenBy(t => t.Tid))
                {
                    bool named = !string.IsNullOrWhiteSpace(t.Description) && t.Description != "(unnamed)";
                    var row = new ThreadRow
                    {
                        Tid = t.Tid,
                        Base = t.Base,
                        Description = t.Description,
                        StartAddress = t.StartAddress,
                        CurrentText = t.RelativeText,
                        IsNamed = named,
                        ProcessName = _processName,
                    };

                    try { row.BoostAllowed = await Tuner.GetBoostAsync((uint)t.Tid); } catch { row.BoostAllowed = true; }
                    if (Services.BoostPreferenceService.Instance.IsSuppressed(_processName, t.Tid, t.Description, t.StartAddress))
                        row.BoostAllowed = false;
                    try
                    {
                        using var h = NativeMethods.Handles.OpenThread(
                            NativeMethods.ThreadAccess.QueryInformation, false, (uint)t.Tid);
                        if (h.IsInvalid) throw new UnauthorizedAccessException();
                        int level = NativeMethods.Priority.GetThreadPriority(h);
                        row.CurrentLevel = level;
                        row.CurrentText = DescribePriority(level);
                    }
                    catch
                    {
                        row.CanEdit = false;
                        anyLocked = true;
                    }
                    Rows.Add(row);
                }
                HeaderText.Text = $"{Rows.Count} threads running";
                if (anyLocked)
                    EditorStatus("Some threads are protected - Windows blocks tuning them.", true);
                else
                    EditorStatus(null, false);
                if (Rows.Count == 0)
                    HeaderText.Text = "No threads readable (process may have exited or access was denied).";
            }
            catch (Exception ex)
            {
                HeaderText.Text = "Failed to read threads.";
                EditorStatus(ex.Message, true);
            }
        }
        finally
        {
            _loadingList = false;
        }
    }

    private ThreadRow? _contextProcessRow;

    private void Row_ContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (sender is FrameworkElement target && target.DataContext is ThreadRow row)
        {
            _contextProcessRow = row;
        }
    }

    private void ThreadMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout menu && _contextProcessRow != null)
        {
            _ = UpdateThreadMenuStateAsync(menu, _contextProcessRow);
        }
    }

    private async Task UpdateThreadMenuStateAsync(MenuFlyout menu, ThreadRow row)
    {
        // Disable mutating actions if thread is protected
        foreach (var item in menu.Items)
        {
            string text = (item as MenuFlyoutItem)?.Text ?? (item as MenuFlyoutSubItem)?.Text ?? "";
            
            if (item is Control c) 
                c.IsEnabled = row.CanEdit;
        }

        var priority = menu.Items.OfType<MenuFlyoutSubItem>()
            .FirstOrDefault(x => x.Text.StartsWith("Priority", StringComparison.OrdinalIgnoreCase));
        if (priority != null)
        {
            priority.Text = $"Priority  (current: {DescribePriority(row.CurrentLevel).Split(' ')[0]})";
            foreach (var item in priority.Items.OfType<MenuFlyoutItem>())
            {
                string tagStr = item.Tag?.ToString() ?? "";
                if (int.TryParse(tagStr, out int l) && l == row.CurrentLevel)
                {
                    if (!item.Text.EndsWith("✓")) item.Text += "  ✓";
                }
                else
                {
                    item.Text = item.Text.Replace("  ✓", "");
                }
            }
        }

        try
        {
            bool boostEnabled = row.BoostAllowed;
            var boost = menu.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(x => x.Text.StartsWith("Priority boost", StringComparison.OrdinalIgnoreCase));
            if (boost != null)
            {
                boost.Text = $"Priority boost  (current: {(boostEnabled ? "Enabled" : "Disabled")})";
                foreach (var item in boost.Items.OfType<MenuFlyoutItem>())
                {
                    if (item.Tag?.ToString() == "Enabled")
                        item.Text = boostEnabled ? "Enabled  ✓" : "Enabled";
                    else
                        item.Text = !boostEnabled ? "Disabled  ✓" : "Disabled";
                }
            }
        }
        catch { }

        try
        {
            bool efficiencyEnabled = await Tuner.GetEfficiencyAsync((uint)row.Tid);
            var efficiency = menu.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(x => x.Text.StartsWith("Efficiency mode", StringComparison.OrdinalIgnoreCase));
            if (efficiency != null)
            {
                efficiency.Text = $"Efficiency mode  (current: {(efficiencyEnabled ? "Enabled" : "Disabled")})";
                foreach (var item in efficiency.Items.OfType<MenuFlyoutItem>())
                {
                    if (item.Tag?.ToString() == "Enabled")
                        item.Text = efficiencyEnabled ? "Enabled  ✓" : "Enabled";
                    else
                        item.Text = !efficiencyEnabled ? "Disabled  ✓" : "Disabled";
                }
            }
        }
        catch { }
        
        var suspendItem = menu.Items.OfType<MenuFlyoutItem>()
            .FirstOrDefault(x => x.Text.StartsWith("Suspend", StringComparison.OrdinalIgnoreCase) || x.Text.StartsWith("Resume", StringComparison.OrdinalIgnoreCase));
        if (suspendItem != null)
        {
            suspendItem.Text = row.Suspended ? "Resume" : "Suspend";
        }
    }

    private ThreadRow? GetRow(object sender) 
        => (sender as FrameworkElement)?.DataContext as ThreadRow ?? _contextProcessRow;

    private async void Action_Priority(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row) return;
        if (sender is FrameworkElement { Tag: string p } && int.TryParse(p, out int level))
        {
            try
            {
                await Tuner.SetPriorityAsync((uint)row.Tid, level);
                row.CurrentLevel = level;
                row.CurrentText = DescribePriority(level);
                EditorStatus(null, false);
            }
            catch (Exception ex) { EditorStatus($"Priority: {Short(ex)}", true); }
        }
    }

    private async void Action_Boost(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row) return;
        if (sender is FrameworkElement { Tag: string tag })
        {
            bool want = tag == "Enabled";
            try
            {
                if (want)
                    await Services.BoostPreferenceService.Instance.RestoreAsync(
                        _processName, row.Tid, row.Description, row.StartAddress);
                else
                    await Services.BoostPreferenceService.Instance.SuppressAsync(
                        _processName, row.Tid, row.Description, row.StartAddress);
                
                row.BoostAllowed = want;
                EditorStatus(null, false);
            }
            catch (Exception ex) { EditorStatus($"Boost: {Short(ex)}", true); }
        }
    }

    private async void Action_Eco(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row) return;
        if (sender is FrameworkElement { Tag: string tag })
        {
            bool want = tag == "Enabled";
            try
            {
                await Tuner.SetEfficiencyAsync((uint)row.Tid, want);
                EditorStatus(null, false);
            }
            catch (Exception ex) { EditorStatus($"Efficiency mode: {Short(ex)}", true); }
        }
    }

    private async void Action_Affinity(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row) return;

        try
        {
            ulong currentMask = 0;
            try { currentMask = (await Tuner.GetAffinityStateAsync((uint)row.Tid)).Mask; } catch { }

            int cpus = Math.Min(Environment.ProcessorCount, 64);
            var panel = new StackPanel { Spacing = 8 };
            var checkboxes = new System.Collections.Generic.List<CheckBox>();

            for (int i = 0; i < cpus; i++)
            {
                bool isSet = (currentMask & (1UL << i)) != 0;
                var cb = new CheckBox { Content = $"CPU {i}", IsChecked = isSet, Tag = i };
                checkboxes.Add(cb);
                panel.Children.Add(cb);
            }

            var dialog = new ContentDialog
            {
                Title = $"CPU Affinity: {row.Tid}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 400 },
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ulong newMask = 0;
                foreach (var cb in checkboxes)
                {
                    if (cb.IsChecked == true && cb.Tag is int bit) newMask |= (1UL << bit);
                }
                if (newMask == 0)
                {
                    EditorStatus("Affinity: tick at least one CPU.", true);
                    return;
                }
                await Tuner.SetAffinityAsync((uint)row.Tid, null, newMask);
                EditorStatus(null, false);
            }
        }
        catch (Exception ex) { EditorStatus($"Affinity: {Short(ex)}", true); }
    }

    private async void Action_Ideal(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row) return;

        try
        {
            int currentIdeal = -1;
            try { currentIdeal = (await Tuner.GetIdealProcessorAsync((uint)row.Tid)).Number; } catch { }

            int cpus = Math.Min(Environment.ProcessorCount, 64);
            var panel = new StackPanel { Spacing = 8 };
            var radioButtons = new System.Collections.Generic.List<RadioButton>();

            for (int i = 0; i < cpus; i++)
            {
                var rb = new RadioButton { Content = $"CPU {i}", IsChecked = (i == currentIdeal), Tag = i };
                radioButtons.Add(rb);
                panel.Children.Add(rb);
            }

            var dialog = new ContentDialog
            {
                Title = $"Ideal Processor: {row.Tid}",
                Content = new ScrollViewer { Content = panel, MaxHeight = 400 },
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var selected = radioButtons.FirstOrDefault(r => r.IsChecked == true);
                if (selected != null && selected.Tag is int cpu)
                {
                    await Tuner.SetIdealProcessorAsync((uint)row.Tid, 0, (byte)cpu);
                    EditorStatus(null, false);
                }
            }
        }
        catch (Exception ex) { EditorStatus($"Ideal processor: {Short(ex)}", true); }
    }

    private async void Action_Suspend(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row) return;
        try
        {
            if (row.Suspended)
            {
                await Tuner.ResumeThreadAsync((uint)row.Tid);
                row.Suspended = false;
            }
            else
            {
                await Tuner.SuspendThreadAsync((uint)row.Tid);
                row.Suspended = true;
            }
            EditorStatus(null, false);
        }
        catch (Exception ex) { EditorStatus($"Suspend/resume: {Short(ex)}", true); }
    }

    private async void Action_Terminate(object sender, RoutedEventArgs e)
    {
        if (GetRow(sender) is not { } row) return;
        
        var dialog = new ContentDialog
        {
            Title = "End thread",
            Content = new TextBlock { Text = $"Are you sure you want to terminate thread {row.Tid}? Ending a thread can crash the process.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Terminate",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await Tuner.TerminateThreadAsync((uint)row.Tid);
                EditorStatus($"TID {row.Tid} terminated - refreshing list.", false);
                await LoadThreadsAsync();
            }
            catch (Exception ex) { EditorStatus($"End thread: {Short(ex)}", true); }
        }
    }

    private void EditorStatus(string? message, bool warn)
    {
        if (string.IsNullOrEmpty(message))
        {
            EditorStatusBox.Visibility = Visibility.Collapsed;
            EditorStatusBox.Text = string.Empty;
        }
        else
        {
            EditorStatusBox.Text = message;
            EditorStatusBox.Visibility = Visibility.Visible;
            if (!warn)
                EditorStatusBox.Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorSuccessBrush"];
            else
                EditorStatusBox.Foreground = (SolidColorBrush)Application.Current.Resources["SystemFillColorCautionBrush"];
        }
    }

    private static string DescribePriority(int level) => level switch
    {
        -15 => "Idle (-15)",
        -2 => "Lowest (-2)",
        -1 => "Below normal (-1)",
        0 => "Normal (0)",
        1 => "Above normal (+1)",
        2 => "Highest (+2)",
        15 => "Time critical (+15)",
        > 2 => $"High ({level})",
        _ => $"Level {level}",
    };

    private static string Short(Exception ex) =>
        ex is UnauthorizedAccessException
            ? "Access denied - protected thread."
            : ex.Message.Split('\n')[0];
}
