using System.Collections.ObjectModel;

namespace kaliteConfig.Models
{
    public class EtwSessionGroup
    {
        public string Category { get; set; } = string.Empty;
        public ObservableCollection<EtwSessionModel> Sessions { get; } = new();
    }
}
