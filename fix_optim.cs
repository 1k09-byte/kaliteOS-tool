using System;
using System.IO;

class Program
{
    static void Main()
    {
        string path = @"src\kaliteConfig\ViewModels\AffinityViewModel.cs";
        string content = File.ReadAllText(path);
        
        int startIdx = content.IndexOf("private async Task OptimizeAsync()");
        if (startIdx == -1) return;

        // Find the next [RelayCommand] to know where OptimizeAsync ends
        int nextCmd = content.IndexOf("[RelayCommand]", startIdx);
        if (nextCmd == -1) nextCmd = content.Length;

        // Extract everything before the method and after the method
        string before = content.Substring(0, startIdx);
        string after = content.Substring(nextCmd);

        string newBody = @"private async Task OptimizeAsync()
        {
            IsOptimizing = true;
            try
            {
                await Task.Delay(50);
                ApplyOptimizerToCollection(GraphicsDevices, kaliteConfig.Services.AffinityTargetType.GPU);
                ApplyOptimizerToCollection(UsbDevices, kaliteConfig.Services.AffinityTargetType.USB);
                ApplyOptimizerToCollection(NetworkDevices, kaliteConfig.Services.AffinityTargetType.WiFi);
                
                await RestartPendingAsync();
            }
            finally
            {
                IsOptimizing = false;
            }
        }

        private void ApplyOptimizerToCollection(System.Collections.ObjectModel.ObservableCollection<kaliteConfig.Models.AffinityDeviceItem> collection, kaliteConfig.Services.AffinityTargetType target)
        {
            ulong mask = kaliteConfig.Services.AffinityOptimizerService.CalculateOptimalMask(target);
            if (mask == 0) return;

            foreach (var item in collection)
            {
                if (!IsDevicePresent(item)) continue;
                item.SelectedPolicy = ""IrqPolicySpecifiedProcessors"";
                foreach (var group in item.CoreGroups)
                {
                    foreach (var thread in group.Threads)
                    {
                        thread.IsChecked = (mask & (1UL << thread.Index)) != 0;
                    }
                }
                ApplyDeviceChanges(item);
                _pendingRestartIds.Add(item.DeviceInstanceId);
                RequiresRestart = true;
                _ = LoadDeviceDetailsAsync(item);
            }
        }

        ";

        File.WriteAllText(path, before + newBody + after);
    }
}
