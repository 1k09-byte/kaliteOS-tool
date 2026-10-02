using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;
using Microsoft.UI.Dispatching;


namespace kaliteConfig.ViewModels
{
    public partial class EtwManagerViewModel : ObservableObject
    {
        private readonly EtwManagerService _etwService;
        private readonly DispatcherQueue _dispatcher;
        private List<EtwSessionModel> _allSessionsCache = new();

        [ObservableProperty]
        public partial ObservableCollection<EtwSessionGroup> GroupedSessions { get; set; } = new();

        [ObservableProperty]
        public partial string SearchQuery { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsRefreshing { get; set; }

        public EtwManagerViewModel()
        {
            _etwService = new EtwManagerService();
            _dispatcher = DispatcherQueue.GetForCurrentThread();
        }

        [RelayCommand]
        public async Task RefreshAsync()
        {
            if (IsRefreshing) return;
            IsRefreshing = true;

            var list = await Task.Run(() => _etwService.GetActiveSessions());

            _dispatcher.TryEnqueue(() =>
            {
                _allSessionsCache = list.OrderByDescending(x => x.BuffersWritten).ToList();
                ApplyFilter();
                
                // Small delay lets the ListView finish rendering before
                // we un-suppress toggle events (ensures Toggled from
                // data-binding initialization is fully drained).
                _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    IsRefreshing = false;
                });
            });
        }

        partial void OnSearchQueryChanged(string value)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var filtered = string.IsNullOrWhiteSpace(SearchQuery) 
                ? _allSessionsCache 
                : _allSessionsCache.Where(x => x.Name.Contains(SearchQuery, System.StringComparison.OrdinalIgnoreCase)).ToList();

            var sorted = filtered.OrderBy(x => x.Category).ThenByDescending(x => x.BuffersWritten).ToList();
            var grouped = sorted.GroupBy(x => x.Category);

            GroupedSessions.Clear();
            foreach (var g in grouped)
            {
                var group = new EtwSessionGroup { Category = g.Key };
                foreach (var session in g)
                {
                    group.Sessions.Add(session);
                }
                GroupedSessions.Add(group);
            }
        }

        public async Task StartSessionAsync(EtwSessionModel model)
        {
            if (model == null) return;

            bool success = await Task.Run(() => _etwService.StartSession(model.Name));

            _dispatcher.TryEnqueue(() =>
            {
                if (success)
                {
                    model.IsRunning = true;
                    // Automatically enable autologger so it survives reboot
                    ToggleAutoLogger(model, true);
                }
                else
                {
                    // Failed to start — snap the toggle back to OFF.
                    model.IsRunning = false;
                }
            });
        }

        /// <summary>
        /// Called from the page code-behind when the user flips the
        /// "State" toggle OFF. Runs logman on a background thread
        /// so the UI never freezes.
        /// </summary>
        public async Task StopSessionAsync(EtwSessionModel model)
        {
            if (model == null) return;

            bool success = await Task.Run(() => _etwService.StopSession(model.Name));

            _dispatcher.TryEnqueue(() =>
            {
                if (success)
                {
                    model.IsRunning = false;
                    model.BuffersWritten = 0;
                    // Automatically disable autologger so it stays dead on reboot
                    ToggleAutoLogger(model, false);
                }
                else
                {
                    // Failed to stop — snap the toggle back to ON.
                    model.IsRunning = true;
                }
            });
        }

        public void ToggleAutoLogger(EtwSessionModel model, bool isOn)
        {
            if (model == null) return;

            bool success = isOn
                ? _etwService.EnableAutoLogger(model.Name)
                : _etwService.DisableAutoLogger(model.Name);

            if (success)
            {
                // Explicitly update property so programmatic calls from StopSession sync the UI
                model.IsAutoLoggerEnabled = isOn;
            }
            else
            {
                // Registry write failed (e.g. TrustedInstaller lock).
                // Snap back to previous state.
                model.IsAutoLoggerEnabled = !isOn;
            }
        }

        [RelayCommand]
        public async Task ClearAllLogsAsync()
        {
            IsRefreshing = true;
            await Task.Run(() => _etwService.ClearAllEventLogs());
            IsRefreshing = false;
        }

        [RelayCommand]
        public async Task OptimizeAllAsync()
        {
            IsRefreshing = true;
            await Task.Run(() => _etwService.OptimizeAllSessions());
            IsRefreshing = false;
        }

        [RelayCommand]
        public async Task RevertDefaultAsync()
        {
            IsRefreshing = true;
            await Task.Run(() => _etwService.RevertAllSessionsToDefault());
            IsRefreshing = false;
        }
    }
}
