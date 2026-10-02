using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.Models
{
    public partial class EtwSessionModel : ObservableObject
    {
        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string LogFilePath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsRunning { get; set; }

        [ObservableProperty]
        public partial uint BuffersWritten { get; set; }

        [ObservableProperty]
        public partial bool IsAutoLoggerEnabled { get; set; }
        
        [ObservableProperty]
        public partial bool CanToggle { get; set; } = true;

        [ObservableProperty]
        public partial string Description { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Category { get; set; } = "Other";
    }
}
