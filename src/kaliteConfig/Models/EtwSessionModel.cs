using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.Models
{
    public partial class EtwSessionModel : ObservableObject
    {
        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string logFilePath = string.Empty;

        [ObservableProperty]
        private bool isRunning;

        [ObservableProperty]
        private uint buffersWritten;

        [ObservableProperty]
        private bool isAutoLoggerEnabled;
        
        [ObservableProperty]
        private bool canToggle = true;

        [ObservableProperty]
        private string description = string.Empty;

        [ObservableProperty]
        private string category = "Other";
    }
}
